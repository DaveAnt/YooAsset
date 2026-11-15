using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace YooAsset.Editor
{
    /// <summary>
    /// 只由相对路径与原始输入字节计算 bundle 名称；不读取构建产物、时间戳或机器绝对路径。
    /// </summary>
    internal static class SourceBundleHash
    {
        /// <summary>v1 格式标识；其后每条记录为 pathLength32LE、UTF8 path、length64LE、原始内容。</summary>
        private static readonly byte[] s_Format = Encoding.UTF8.GetBytes("GameMain.SourceBundleHash\0v1\0");
        /// <summary>路径使用无 BOM、严格 UTF8。</summary>
        private static readonly Encoding s_Utf8 = new UTF8Encoding(false, true);

        /// <summary>计算非空源集合的 SHA256；同路径同内容去重，同路径不同内容拒绝。</summary>
        internal static string Compute(IEnumerable<Source> sources)
        {
            if (sources == null)
                throw new ArgumentException("sources must contain at least one source record.", nameof(sources));

            var sorted = new SortedDictionary<string, Source>(StringComparer.Ordinal);
            foreach (Source source in sources)
            {
                if (source == null)
                    throw new ArgumentException("sources must not contain null records.", nameof(sources));
                if (sorted.TryGetValue(source.RelativePath, out Source previous))
                {
                    if (!HaveSameContent(previous, source))
                        throw new InvalidOperationException("Conflicting source content for relative path: " + source.RelativePath);
                }
                else
                    sorted.Add(source.RelativePath, source);
            }
            if (sorted.Count == 0)
                throw new ArgumentException("sources must contain at least one source record; AB hash fallback is forbidden.", nameof(sources));

            using (SHA256 hash = SHA256.Create())
            using (var output = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write))
            using (var writer = new BinaryWriter(output, s_Utf8, true))
            {
                writer.Write(s_Format);
                foreach (Source source in sorted.Values)
                {
                    byte[] path = s_Utf8.GetBytes(source.RelativePath);
                    using (Stream input = source.OpenRead())
                    {
                        writer.Write(path.Length);
                        writer.Write(path);
                        writer.Write(input.Length);
                        writer.Flush();
                        input.CopyTo(output);
                    }
                }
                output.FlushFinalBlock();
                return BitConverter.ToString(hash.Hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        /// <summary>建立旧 bundle 名到源哈希文件名的映射；不同 bundle 不允许覆盖同一目标文件。</summary>
        internal static Dictionary<string, string> CreateBundleNames(IDictionary<string, IEnumerable<Source>> bundles)
        {
            if (bundles == null || bundles.Count == 0)
                throw new ArgumentException("bundles must contain at least one bundle with source inputs.", nameof(bundles));
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            var owners = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var bundle in bundles)
            {
                if (string.IsNullOrWhiteSpace(bundle.Key))
                    throw new ArgumentException("bundles contains an empty bundle name.", nameof(bundles));
                string target = Compute(bundle.Value) + ".bundle";
                if (owners.TryGetValue(target, out string owner))
                    throw new InvalidOperationException($"Source hash target conflict: '{owner}' and '{bundle.Key}' both require '{target}'. Distinct bundle identities cannot share an output file.");
                owners.Add(target, bundle.Key);
                names.Add(bundle.Key, target);
            }
            return names;
        }

        /// <summary>统一 slash，保留大小写，并拒绝非规范相对路径，避免机器路径进入哈希。</summary>
        internal static string NormalizePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new ArgumentException("relativePath must be a nonempty relative file path.", nameof(relativePath));
            string path = relativePath.Replace('\\', '/');
            if (path.StartsWith("/", StringComparison.Ordinal) || path.IndexOf(':') >= 0)
                throw new ArgumentException("relativePath must not be absolute: " + relativePath, nameof(relativePath));
            foreach (string segment in path.Split('/'))
            {
                if (segment.Length == 0 || segment == "." || segment == "..")
                    throw new ArgumentException("relativePath must not contain empty, '.' or '..' segments: " + relativePath, nameof(relativePath));
            }
            return path;
        }

        /// <summary>同一输入直接去重；不同输入仍比较原始字节，不依赖 mtime 或哈希碰撞。</summary>
        private static bool HaveSameContent(Source left, Source right)
        {
            if (left.SharesInputWith(right))
                return true;

            using (Stream first = left.OpenRead())
            using (Stream second = right.OpenRead())
            {
                if (first.Length != second.Length)
                    return false;
                var a = new byte[8192];
                var b = new byte[8192];
                int count;
                while ((count = first.Read(a, 0, a.Length)) != 0)
                {
                    int received = 0;
                    while (received < count)
                    {
                        int read = second.Read(b, received, count - received);
                        if (read == 0)
                            return false;
                        received += read;
                    }
                    for (int index = 0; index < count; index++)
                    {
                        if (a[index] != b[index])
                            return false;
                    }
                }
                return second.ReadByte() == -1;
            }
        }

        /// <summary>一条源输入；filePath 与 bytes 二选一，bytes 支持内置资源的虚拟 metadata。</summary>
        internal sealed class Source
        {
            /// <summary>原始文件路径，仅用于 IO，不参与哈希。</summary>
            private readonly string m_FilePath;
            /// <summary>内存输入，在 Compute 完成前由调用方保证不修改。</summary>
            private readonly byte[] m_Bytes;
            /// <summary>稳定的相对路径。</summary>
            private readonly string m_RelativePath;

            /// <summary>规范化后的源相对路径。</summary>
            internal string RelativePath
            {
                get { return m_RelativePath; }
            }

            /// <summary>建立文件或内存记录；空文件合法，缺文件以及缺/双输入明确失败。</summary>
            internal Source(string relativePath, string filePath = null, byte[] bytes = null)
            {
                m_RelativePath = NormalizePath(relativePath);
                if ((filePath == null) == (bytes == null))
                    throw new ArgumentException("Exactly one of filePath or bytes is required for source: " + m_RelativePath);
                if (filePath != null && !File.Exists(filePath))
                    throw new FileNotFoundException("Required source file is missing: " + m_RelativePath, filePath);
                m_FilePath = filePath;
                m_Bytes = bytes;
            }

            /// <summary>计算期间输入保持不变；相同文件或内存实例无需重复读取比较。</summary>
            internal bool SharesInputWith(Source other)
            {
                return ReferenceEquals(this, other)
                    || (m_Bytes != null && ReferenceEquals(m_Bytes, other.m_Bytes))
                    || (m_FilePath != null && string.Equals(m_FilePath, other.m_FilePath, StringComparison.Ordinal));
            }

            /// <summary>调用方拥有返回流，文件 IO 失败原样传播。</summary>
            internal Stream OpenRead()
            {
                return m_Bytes != null
                    ? (Stream)new MemoryStream(m_Bytes, false)
                    : new FileStream(m_FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
        }
    }
}

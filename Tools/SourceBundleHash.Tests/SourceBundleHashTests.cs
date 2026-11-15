using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using YooAsset.Editor;
using Xunit;

namespace YooAsset.SourceHash.Tests
{
    /// <summary>真实源哈希 helper 的纯 .NET 契约测试，不复制生产算法。</summary>
    public sealed class SourceBundleHashTests
    {
        /// <summary>枚举顺序不影响结果，路径 slash 统一且重复记录去重。</summary>
        [Fact]
        public void OrderSlashAndIdenticalDuplicatesDoNotChangeHash()
        {
            var a = Memory("Assets/Z.asset", "z");
            var b = Memory("Assets/A.asset", "a");
            Assert.Equal(Hash(a, b), Hash(b, a, Memory("Assets\\A.asset", "a")));
        }

        /// <summary>真实文件所在机器目录和修改时间不参与源哈希。</summary>
        [Fact]
        public void MachineRootAndModificationTimeDoNotChangeHash()
        {
            string root = Path.Combine(Path.GetTempPath(), "SourceBundleHashTests-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "machineA"));
                Directory.CreateDirectory(Path.Combine(root, "machineB"));
                string a = Path.Combine(root, "machineA", "asset");
                string b = Path.Combine(root, "machineB", "asset");
                File.WriteAllText(a, "same raw input");
                File.WriteAllText(b, "same raw input");
                File.SetLastWriteTimeUtc(a, new DateTime(2000, 1, 1));
                Assert.Equal(Hash(new SourceBundleHash.Source("Assets/asset", a)), Hash(new SourceBundleHash.Source("Assets/asset", b)));
                Assert.Equal(Hash(new SourceBundleHash.Source("Assets/asset", a)), Hash(Memory("Assets/asset", "same raw input")));
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }

        /// <summary>内容、meta 导入设置、路径与路径大小写均是源输入。</summary>
        [Theory]
        [InlineData("Assets/a.asset", "new", "Assets/a.asset.meta", "meta")]
        [InlineData("Assets/a.asset", "raw", "Assets/a.asset.meta", "changed importer")]
        [InlineData("Assets/moved.asset", "raw", "Assets/a.asset.meta", "meta")]
        [InlineData("Assets/A.asset", "raw", "Assets/a.asset.meta", "meta")]
        public void SourceChangesInvalidateHash(string path, string content, string metaPath, string meta)
        {
            Assert.NotEqual(Hash(Memory("Assets/a.asset", "raw"), Memory("Assets/a.asset.meta", "meta")),
                Hash(Memory(path, content), Memory(metaPath, meta)));
        }

        /// <summary>固定格式使用 v1 域标识、UTF8 路径长度、64位小端内容长度，不拼接歧义。</summary>
        [Fact]
        public void FramingHasExplicitVersionPathsAndContentBoundaries()
        {
            byte[] input;
            using (var output = new MemoryStream())
            using (var writer = new BinaryWriter(output, new UTF8Encoding(false)))
            {
                writer.Write(Encoding.UTF8.GetBytes("GameMain.SourceBundleHash\0v1\0"));
                byte[] path = Encoding.UTF8.GetBytes("Assets/中文.asset");
                writer.Write(path.Length);
                writer.Write(path);
                writer.Write(3L);
                writer.Write(new byte[] { 0, 128, 255 });
                writer.Flush();
                input = output.ToArray();
            }
            using (SHA256 sha = SHA256.Create())
            {
                string expected = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
                Assert.Equal(expected, Hash(new SourceBundleHash.Source("Assets/中文.asset", bytes: new byte[] { 0, 128, 255 })));
            }
            Assert.NotEqual(Hash(Memory("a", "bc")), Hash(Memory("ab", "c")));
            Assert.NotEqual(Hash(Memory("a", "b"), Memory("c", "d")), Hash(Memory("a", "bc"), Memory("d", "")));
        }

        /// <summary>独立 Python 小端编码生成的固定测试向量，各文化与重复输入必须保持 v1 结果。</summary>
        [Theory]
        [InlineData("en-US")]
        [InlineData("zh-CN")]
        [InlineData("tr-TR")]
        [InlineData("ar-SA")]
        public void FixedV1DigestIsIndependentOfCulture(string culture)
        {
            CultureInfo previous = CultureInfo.CurrentCulture;
            CultureInfo previousUi = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture;
                byte[] bytes = { 0, 128, 255 };
                var source = new SourceBundleHash.Source("Assets/中文.asset", bytes: bytes);
                const string expected = "d8f924aec7191e3732de601c479e1c2ba655ebfe65d8ca5f297aaa8c66738f72";
                Assert.Equal(expected, Hash(source));
                Assert.Equal(expected, Hash(source, source, new SourceBundleHash.Source("Assets\\中文.asset", bytes: bytes)));
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
                CultureInfo.CurrentUICulture = previousUi;
            }
        }

        /// <summary>同一物理输入直接去重，不同文件和不同数组不能被误判为同一输入。</summary>
        [Fact]
        public void IdenticalInputReferencesAreDeduplicatedBeforeReading()
        {
            string file = Path.Combine(Path.GetTempPath(), "SourceBundleHashTests-" + Guid.NewGuid().ToString("N"));
            byte[] bytes = { 0, 128, 255 };
            try
            {
                File.WriteAllBytes(file, bytes);
                var source = new SourceBundleHash.Source("Assets/中文.asset", file);
                var duplicate = new SourceBundleHash.Source("Assets\\中文.asset", file);
                var memory = new SourceBundleHash.Source("Assets/中文.asset", bytes: bytes);
                Assert.True(source.SharesInputWith(source));
                Assert.True(source.SharesInputWith(duplicate));
                Assert.True(memory.SharesInputWith(new SourceBundleHash.Source("Assets/中文.asset", bytes: bytes)));
                Assert.False(source.SharesInputWith(memory));
                Assert.False(memory.SharesInputWith(new SourceBundleHash.Source("Assets/中文.asset", bytes: (byte[])bytes.Clone())));
                Assert.Equal(Hash(source), Hash(source, duplicate));
                Assert.Equal(Hash(source), Hash(memory));
            }
            finally
            {
                File.Delete(file);
            }
        }

        /// <summary>内置原容器与虚拟 metadata 都参与计算，不能退回 AB 哈希。</summary>
        [Fact]
        public void BuiltinContainerAndVirtualMetadataAreBothInputs()
        {
            const string path = "Unity/Resources/unity_builtin_extra";
            var source = Memory(path, "real container bytes");
            var meta = Memory(path + ".virtual-meta", "f0000000000000000000000000000000/7/0/unity_builtin_extra");
            string hash = Hash(source, meta);
            Assert.NotEqual(hash, Hash(Memory(path, "changed container"), meta));
            Assert.NotEqual(hash, Hash(source, Memory(path + ".virtual-meta", "f0000000000000000000000000000000/8/0/unity_builtin_extra")));
            Assert.Equal(hash, Hash(meta, source));
        }

        /// <summary>同规范 key 但不同字节不允许任取一条。</summary>
        [Fact]
        public void ConflictingDuplicateSourcesFail()
        {
            Assert.Throws<InvalidOperationException>(() => Hash(Memory("Assets/a", "x"), Memory("Assets\\a", "y")));
        }

        /// <summary>不同 bundle 的相同 sourcehash 明确拒绝，防止不同 AB 字节静默覆盖。</summary>
        [Fact]
        public void ConflictingTargetBundleNamesFail()
        {
            var bundles = new Dictionary<string, IEnumerable<SourceBundleHash.Source>>
            {
                ["first"] = new[] { Memory("Assets/a", "x") },
                ["second"] = new[] { Memory("Assets/a", "x") }
            };
            Assert.Throws<InvalidOperationException>(() => SourceBundleHash.CreateBundleNames(bundles));
            bundles["second"] = new[] { Memory("Assets/b", "x") };
            var names = SourceBundleHash.CreateBundleNames(bundles);
            Assert.Equal(Hash(Memory("Assets/a", "x")) + ".bundle", names["first"]);
            Assert.Equal(2, names.Values.Distinct(StringComparer.Ordinal).Count());
        }

        /// <summary>缺必需输入明确失败，空文件是合法且被 framing 区分的源记录。</summary>
        [Fact]
        public void MissingInputsFailButEmptyFilesAreValid()
        {
            Assert.Throws<ArgumentException>(() => SourceBundleHash.Compute(null));
            Assert.Throws<ArgumentException>(() => Hash());
            Assert.Throws<ArgumentException>(() => Hash((SourceBundleHash.Source)null));
            Assert.Throws<ArgumentException>(() => new SourceBundleHash.Source("Assets/a"));
            Assert.Throws<ArgumentException>(() => new SourceBundleHash.Source("Assets/a", "file", Array.Empty<byte>()));
            Assert.Throws<FileNotFoundException>(() => new SourceBundleHash.Source("Assets/a", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));
            Assert.Throws<ArgumentException>(() => SourceBundleHash.CreateBundleNames(new Dictionary<string, IEnumerable<SourceBundleHash.Source>>()));
            Assert.Throws<ArgumentException>(() => SourceBundleHash.CreateBundleNames(new Dictionary<string, IEnumerable<SourceBundleHash.Source>> { ["bundle"] = Array.Empty<SourceBundleHash.Source>() }));
            Assert.NotEqual(Hash(Memory("Assets/a", "")), Hash(Memory("Assets/b", "")));
        }

        /// <summary>绝对路径、上级跳转等非稳定 key 被拒绝。</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData("C:/machine/a")]
        [InlineData("/machine/a")]
        [InlineData("\\\\server\\a")]
        [InlineData("Assets/../a")]
        [InlineData("Assets/./a")]
        [InlineData("Assets//a")]
        [InlineData("Assets/a/")]
        public void InvalidRelativePathsFail(string path)
        {
            Assert.Throws<ArgumentException>(() => Memory(path, "bytes"));
        }

        /// <summary>大文件使用真实流路径且与内存源一致。</summary>
        [Fact]
        public void LargeFileStreamMatchesMemoryInput()
        {
            string file = Path.Combine(Path.GetTempPath(), "SourceBundleHashTests-" + Guid.NewGuid().ToString("N"));
            byte[] bytes = Enumerable.Range(0, 131079).Select(index => (byte)(index % 251)).ToArray();
            try
            {
                File.WriteAllBytes(file, bytes);
                Assert.Equal(Hash(new SourceBundleHash.Source("Assets/large", file)), Hash(new SourceBundleHash.Source("Assets/large", bytes: bytes)));
            }
            finally
            {
                File.Delete(file);
            }
        }

        /// <summary>创建文本内存源。</summary>
        private static SourceBundleHash.Source Memory(string path, string value)
        {
            return new SourceBundleHash.Source(path, bytes: Encoding.UTF8.GetBytes(value));
        }

        /// <summary>直接调用被 Link 的真实 helper。</summary>
        private static string Hash(params SourceBundleHash.Source[] sources)
        {
            return SourceBundleHash.Compute(sources);
        }
    }
}

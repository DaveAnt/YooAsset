using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Content;
using UnityEditor.Build.Pipeline.Interfaces;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;

namespace YooAsset.Editor
{
    /// <summary>报告之后、包文件复制之前定稿源哈希名称，真实校验与依赖 ID 不变。</summary>
    internal sealed class TaskSourceHashNames : IBuildTask
    {
        /// <summary>源输入证据文件，随补丁目录一起部署。</summary>
        internal const string EvidenceFileName = "source-inputs.json";
        /// <summary>本次同步构建独占的真实源捕获。</summary>
        private readonly SourceCapture m_Capture;

        /// <summary>绑定本次捕获，不注册第二套 BuildMap。</summary>
        internal TaskSourceHashNames(SourceCapture capture)
        {
            m_Capture = capture;
        }

        /// <summary>源码 hash 名称 finalization；所有冲突先检查，再写原官方清单与报告。</summary>
        void IBuildTask.Run(BuildContext context)
        {
            BuildParametersContext parametersContext = context.GetContextObject<BuildParametersContext>();
            BuildParameters parameters = parametersContext.Parameters;
            BuildManifest manifest = context.GetContextObject<ManifestContext>().Manifest;
            BuildMapContext map = context.GetContextObject<BuildMapContext>();
            string output = parametersContext.GetPackageOutputDirectory();
            var inputs = new Dictionary<string, IEnumerable<SourceBundleHash.Source>>(StringComparer.Ordinal);
            foreach (BuildBundle bundle in manifest.BundleList)
            {
                if (!m_Capture.Sources.TryGetValue(bundle.BundleName, out List<SourceBundleHash.Source> sources) || sources.Count == 0)
                    throw new InvalidOperationException("Bundle has no captured original source inputs: " + bundle.BundleName);
                inputs.Add(bundle.BundleName, sources);
            }
            Dictionary<string, string> names = SourceBundleHash.CreateBundleNames(inputs);
            string reportPath = Path.Combine(output, YooAssetConfiguration.GetBuildReportFileName(parameters.PackageName, parameters.PackageVersion));
            BuildReport report = BuildReport.Deserialize(File.ReadAllText(reportPath));
            var evidence = new SourceInputsEvidence { PackageName = parameters.PackageName, PackageVersion = parameters.PackageVersion };

            // BundleList 保持原顺序，BundleID/DependentBundleIDs/ReferrerBundleIDs 与实际 FileHash/CRC/Size 完全不变。
            foreach (BuildBundle bundle in manifest.BundleList)
            {
                string oldName = bundle.BundleName;
                string newName = names[oldName];
                BuildBundleInfo bundleInfo = map.GetBundleInfo(oldName);
                bundleInfo.PackageDestFilePath = Path.Combine(output, newName);
                bundle.BundleName = newName;
                evidence.Bundles.Add(new SourceInputsBundle
                {
                    OriginalBundleName = oldName,
                    BundleName = newName,
                    SourceHash = newName.Substring(0, newName.Length - ".bundle".Length),
                    FileHash = bundle.FileHash,
                    FileCrc = bundle.FileCrc,
                    FileSize = bundle.FileSize,
                    Paths = inputs[oldName].Select(value => value.RelativePath).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray()
                });
            }
            manifest.OutputNameStyle = (int)EFileNameStyle.SourceHashName;
            report.Summary.FileNameStyle = EFileNameStyle.SourceHashName;
            report.Summary.BuiltinShadersBundleName = RemapOptional(report.Summary.BuiltinShadersBundleName, names);
            report.Summary.MonoScriptsBundleName = RemapOptional(report.Summary.MonoScriptsBundleName, names);
            foreach (ReportBundleInfo bundle in report.BundleInfos)
            {
                bundle.BundleName = names[bundle.BundleName];
                bundle.FileName = bundle.BundleName;
                Remap(bundle.DependBundles, names);
                Remap(bundle.ReferenceBundles, names);
            }
            foreach (ReportAssetInfo asset in report.AssetInfos)
            {
                asset.MainBundleName = names[asset.MainBundleName];
                Remap(asset.DependBundles, names);
            }

            string manifestPath = Path.Combine(output, YooAssetConfiguration.GetManifestBinaryFileName(parameters.PackageName, parameters.PackageVersion));
            PackageManifestHelper.SerializeManifestToBinary(manifestPath, manifest, parameters.ManifestEncryptor);
            FileUtility.WriteAllText(Path.Combine(output, YooAssetConfiguration.GetPackageHashFileName(parameters.PackageName, parameters.PackageVersion)), HashUtility.ComputeFileCrc32(manifestPath));
            BuildReport.Serialize(reportPath, report);
            FileUtility.WriteAllText(Path.Combine(output, EvidenceFileName), JsonUtility.ToJson(evidence, true));
            BuildLogger.Log($"Source hash names finalized: {names.Count} bundles in '{output}'.");
        }

        /// <summary>依赖与引用必须存在映射，错链直接失败。</summary>
        private static void Remap(List<string> values, Dictionary<string, string> names)
        {
            for (int index = 0; index < values.Count; index++)
                values[index] = names[values[index]];
        }

        /// <summary>官方 summary 可能填写未实际生成的内置包配置，只有生成过的包才映射。</summary>
        private static string RemapOptional(string name, Dictionary<string, string> names)
        {
            return !string.IsNullOrEmpty(name) && names.TryGetValue(name, out string value) ? value : name;
        }

        /// <summary>由 SBP 实际写入对象及每个主资源的首个文件归属捕获源，不把共享 deps file 归消费包。</summary>
        internal sealed class SourceCapture
        {
            /// <summary>Unity 默认资源容器 GUID。</summary>
            private const string DefaultResourcesGuid = "0000000000000000e000000000000000";
            /// <summary>Unity 内置额外资源容器 GUID。</summary>
            private const string BuiltinExtraGuid = "0000000000000000f000000000000000";
            /// <summary>每个真实 bundle 的普通源与虚拟源。</summary>
            private readonly Dictionary<string, List<SourceBundleHash.Source>> m_Sources = new Dictionary<string, List<SourceBundleHash.Source>>(StringComparer.Ordinal);
            /// <summary>当前项目物理根路径，不进入 hash。</summary>
            private readonly string m_ProjectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

            /// <summary>实际输入证据集合。</summary>
            internal IReadOnlyDictionary<string, List<SourceBundleHash.Source>> Sources
            {
                get { return m_Sources; }
            }

            /// <summary>捕获 callback 的真实数据；未知原输入容器直接失败，不允许 empty 或 AB MD5 fallback。</summary>
            internal void Capture(IWriteData writeData)
            {
                if (!(writeData is IBundleWriteData bundles))
                    throw new InvalidOperationException("SBP PostPackingCallback must provide IBundleWriteData for source hashing.");
                m_Sources.Clear();
                var objects = new Dictionary<string, List<ObjectIdentifier>>(StringComparer.Ordinal);
                var guids = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                foreach (var file in bundles.FileToBundle)
                {
                    string bundle = file.Value;
                    if (!m_Sources.ContainsKey(bundle))
                    {
                        m_Sources.Add(bundle, new List<SourceBundleHash.Source>());
                        objects.Add(bundle, new List<ObjectIdentifier>());
                        guids.Add(bundle, new HashSet<string>(StringComparer.Ordinal));
                    }
                    if (!bundles.FileToObjects.TryGetValue(file.Key, out List<ObjectIdentifier> written))
                        throw new InvalidOperationException($"SBP file '{file.Key}' in bundle '{bundle}' has no FileToObjects input.");
                    objects[bundle].AddRange(written);
                    foreach (ObjectIdentifier identifier in written)
                    {
                        string guid = identifier.guid.ToString();
                        if (!identifier.guid.Empty())
                            guids[bundle].Add(guid);
                        else
                            AddAssetSource(m_Sources[bundle], ResolveObjectPath(identifier.filePath));
                    }
                }

                // AssetToFiles[0] 是主文件；scene 的 ObjectIdentifier 不一定携带 scene 本身，因此必须补主 asset。
                foreach (var asset in bundles.AssetToFiles)
                {
                    if (asset.Value.Count == 0 || !bundles.FileToBundle.TryGetValue(asset.Value[0], out string bundle))
                        throw new InvalidOperationException("SBP main source has no main-file bundle mapping: " + asset.Key);
                    guids[bundle].Add(asset.Key.ToString());
                }

                foreach (var bundle in guids)
                {
                    List<SourceBundleHash.Source> sources = m_Sources[bundle.Key];
                    foreach (string guid in bundle.Value.OrderBy(value => value, StringComparer.Ordinal))
                    {
                        string builtinPath = GetBuiltinPath(guid);
                        if (builtinPath != null)
                        {
                            string physical = Path.Combine(EditorApplication.applicationContentsPath, "Resources", Path.GetFileName(builtinPath));
                            sources.Add(new SourceBundleHash.Source(builtinPath, physical));
                            byte[] metadata = SerializeBuiltinObjects(objects[bundle.Key].Where(value => value.guid.ToString() == guid));
                            sources.Add(new SourceBundleHash.Source(builtinPath + ".virtual-meta", bytes: metadata));
                        }
                        else
                        {
                            string path = AssetDatabase.GUIDToAssetPath(guid);
                            if (string.IsNullOrEmpty(path))
                                throw new InvalidOperationException($"Cannot resolve original source GUID '{guid}' in bundle '{bundle.Key}'. Source hash fallback is forbidden.");
                            AddAssetSource(sources, SourceBundleHash.NormalizePath(path));
                        }
                    }
                }
            }

            /// <summary>普通 asset 必须同时存在源和 meta；注册包用 resolvedPath 读取，key 仍为 Packages/...。</summary>
            private void AddAssetSource(List<SourceBundleHash.Source> sources, string path)
            {
                string physical = Path.Combine(m_ProjectRoot, path);
                if (path.StartsWith("Packages/", StringComparison.Ordinal))
                {
                    PackageInfo package = PackageInfo.FindForAssetPath(path);
                    if (package == null)
                        throw new InvalidOperationException("Cannot resolve package original input: " + path);
                    string prefix = "Packages/" + package.name + "/";
                    if (!path.StartsWith(prefix, StringComparison.Ordinal))
                        throw new InvalidOperationException("Package source path does not match package name: " + path);
                    physical = Path.Combine(package.resolvedPath, path.Substring(prefix.Length));
                }
                sources.Add(new SourceBundleHash.Source(path, physical));
                sources.Add(new SourceBundleHash.Source(path + ".meta", physical + ".meta"));
            }

            /// <summary>无 GUID 的真实文件只接受可映射到项目的输入，不凭生成文件名猜资源。</summary>
            private string ResolveObjectPath(string filePath)
            {
                if (string.IsNullOrEmpty(filePath))
                    throw new InvalidOperationException("SBP object has neither source GUID nor source filePath.");
                string path = filePath.Replace('\\', '/');
                if (Path.IsPathRooted(path))
                {
                    string prefix = m_ProjectRoot.Replace('\\', '/').TrimEnd('/') + "/";
                    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Cannot map SBP absolute object input into a stable project path: " + filePath);
                    path = path.Substring(prefix.Length);
                }
                return SourceBundleHash.NormalizePath(path);
            }

            /// <summary>容器内实际对象身份写入内存 metadata，路径不含 Unity 安装绝对路径。</summary>
            private byte[] SerializeBuiltinObjects(IEnumerable<ObjectIdentifier> identifiers)
            {
                var records = new SortedSet<string>(StringComparer.Ordinal);
                foreach (ObjectIdentifier identifier in identifiers)
                {
                    string path = identifier.filePath ?? string.Empty;
                    path = path.Replace('\\', '/');
                    if (Path.IsPathRooted(path))
                    {
                        string relative = path.StartsWith(EditorApplication.applicationContentsPath.Replace('\\', '/') + "/", StringComparison.OrdinalIgnoreCase)
                            ? "Unity/" + path.Substring(EditorApplication.applicationContentsPath.Length + 1)
                            : ResolveObjectPath(path);
                        path = relative;
                    }
                    records.Add(identifier.guid + "\0" + identifier.localIdentifierInFile.ToString(CultureInfo.InvariantCulture)
                        + "\0" + ((int)identifier.fileType).ToString(CultureInfo.InvariantCulture) + "\0" + path);
                }
                if (records.Count == 0)
                    throw new InvalidOperationException("Builtin source container has no actual ObjectIdentifier metadata.");
                using (var output = new MemoryStream())
                using (var writer = new BinaryWriter(output, new UTF8Encoding(false, true)))
                {
                    writer.Write("GameMain.UnityBuiltinObjects.v1");
                    writer.Write(records.Count);
                    foreach (string record in records)
                        writer.Write(record);
                    writer.Flush();
                    return output.ToArray();
                }
            }

            /// <summary>Unity 的两个真实内置资源容器映射。</summary>
            private static string GetBuiltinPath(string guid)
            {
                if (guid == DefaultResourcesGuid)
                    return "Unity/Resources/unity default resources";
                if (guid == BuiltinExtraGuid)
                    return "Unity/Resources/unity_builtin_extra";
                return null;
            }
        }

        /// <summary>可重验的源输入证据，不包含机器绝对路径。</summary>
        [Serializable]
        internal sealed class SourceInputsEvidence
        {
            /// <summary>证据格式版本。</summary>
            public int Version = 1;
            /// <summary>包名。</summary>
            public string PackageName;
            /// <summary>包版本。</summary>
            public string PackageVersion;
            /// <summary>实际 bundle 映射。</summary>
            public List<SourceInputsBundle> Bundles = new List<SourceInputsBundle>();
        }

        /// <summary>一项源命名和真实 AB 校验证据。</summary>
        [Serializable]
        internal sealed class SourceInputsBundle
        {
            /// <summary>SBP 原包名。</summary>
            public string OriginalBundleName;
            /// <summary>源哈希文件名。</summary>
            public string BundleName;
            /// <summary>原输入 SHA256。</summary>
            public string SourceHash;
            /// <summary>真正 AB MD5，与源哈希独立。</summary>
            public string FileHash;
            /// <summary>真正 AB CRC。</summary>
            public uint FileCrc;
            /// <summary>真正 AB 字节数。</summary>
            public long FileSize;
            /// <summary>参与计算的规范源路径（包含每个 source 的 meta 或 builtin virtual-meta）。</summary>
            public string[] Paths;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using YooAsset.Editor;

namespace YooAsset.Tests.Editor
{
    /// <summary>包内原生接线回归；完整的纯哈希用例由 Tools/SourceBundleHash.Tests 执行。</summary>
    public sealed class SourceHashNameTests
    {
        /// <summary>迁入包内后 v1 固定字节向量必须保持原值。</summary>
        [Test]
        public void V1GoldenDigestRemainsUnchanged()
        {
            var source = new SourceBundleHash.Source("Assets/中文.asset", bytes: new byte[] { 0, 128, 255 });
            Assert.AreEqual("d8f924aec7191e3732de601c479e1c2ba655ebfe65d8ca5f297aaa8c66738f72",
                SourceBundleHash.Compute(new[] { source }));
        }

        /// <summary>旧流程保持 11 个官方任务，源样式仅在报告后插入定稿任务。</summary>
        [Test]
        public void NativeTaskIsInsertedOnlyBetweenReportAndPackage()
        {
            var pipeline = new ScriptableBuildPipeline();
            MethodInfo method = typeof(ScriptableBuildPipeline).GetMethod("GetDefaultBuildPipeline", BindingFlags.Instance | BindingFlags.NonPublic);
            var original = (List<IBuildTask>)method.Invoke(pipeline, new object[] { null });
            var source = (List<IBuildTask>)method.Invoke(pipeline, new object[] { new TaskSourceHashNames.SourceCapture() });
            Assert.AreEqual(11, original.Count);
            Assert.AreEqual(12, source.Count);
            Assert.IsInstanceOf<TaskCreateReport_SBP>(source[7]);
            Assert.IsInstanceOf<TaskSourceHashNames>(source[8]);
            Assert.IsInstanceOf<TaskCreatePackage_SBP>(source[9]);
            for (int index = 0; index < original.Count; index++)
                Assert.AreEqual(original[index].GetType(), source[index < 8 ? index : index + 1].GetType());
        }

        /// <summary>所有非 SBP 参数在准备入口拒绝，不先创建输出或静默回退。</summary>
        [Test]
        public void OtherPipelinesRejectSourceHashNamesAtPreparation()
        {
            BuildParameters[] parameters =
            {
                new LegacyBuildParameters(),
                new RawFileBuildParameters(),
                new ArchiveFileBuildParameters(),
                new EditorSimulateBuildParameters(),
#if TUANJIE_1_8_OR_NEWER && YOOASSET_INSTANT_ASSET_SUPPORT
                new InstantAssetBuildParameters()
#endif
            };
            foreach (BuildParameters value in parameters)
            {
                value.FileNameStyle = EFileNameStyle.SourceHashName;
                Assert.Throws<NotSupportedException>(() => new BuildParametersContext(value).CheckBuildParameters(), value.GetType().Name);
            }
            var mismatched = new ScriptableBuildParameters
            {
                FileNameStyle = EFileNameStyle.SourceHashName,
                BuildPipeline = EBuildPipeline.LegacyBuildPipeline.ToString()
            };
            Assert.Throws<NotSupportedException>(() => new BuildParametersContext(mismatched).CheckBuildParameters());
        }
    }
}

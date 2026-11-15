using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEditor.Build.Pipeline;

namespace YooAsset.Editor
{
    /// <summary>
    /// 可编程构建管线，使用 Scriptable Build Pipeline 进行构建
    /// </summary>
    public class ScriptableBuildPipeline : IBuildPipeline
    {
        /// <summary>全局 SBP callback 为同步借用，拒绝源命名构建并发与重入。</summary>
        private static int s_SourceHashBuilding;

        /// <summary>
        /// 执行构建流程
        /// </summary>
        /// <param name="buildParameters">构建参数</param>
        /// <param name="enableLog">是否启用日志记录</param>
        /// <returns>构建结果</returns>
        public BuildResult Run(BuildParameters buildParameters, bool enableLog)
        {
            if (buildParameters is ScriptableBuildParameters)
            {
                BundleBuilder builder = new BundleBuilder();
                if (buildParameters.FileNameStyle != EFileNameStyle.SourceHashName)
                    return builder.Run(buildParameters, GetDefaultBuildPipeline(null), enableLog);

                if (Interlocked.CompareExchange(ref s_SourceHashBuilding, 1, 0) != 0)
                    throw new InvalidOperationException("SourceHashName cannot build concurrently or reenter while the global SBP callback is borrowed.");

                BuildCallbacks callbacks = ContentPipeline.BuildCallbacks;
                var previous = callbacks.PostPackingCallback;
                try
                {
                    var capture = new TaskSourceHashNames.SourceCapture();
                    callbacks.PostPackingCallback = (build, dependencies, writeData) =>
                    {
                        ReturnCode code = previous == null ? ReturnCode.Success : previous(build, dependencies, writeData);
                        if (code < 0)
                            return code;
                        capture.Capture(writeData);
                        return code;
                    };
                    return builder.Run(buildParameters, GetDefaultBuildPipeline(capture), enableLog);
                }
                finally
                {
                    callbacks.PostPackingCallback = previous;
                    Volatile.Write(ref s_SourceHashBuilding, 0);
                }
            }
            else
            {
                throw new ArgumentException($"Invalid build parameter type: '{buildParameters.GetType().Name}'.", nameof(buildParameters));
            }
        }

        /// <summary>
        /// 获取默认的构建流程
        /// </summary>
        private List<IBuildTask> GetDefaultBuildPipeline(TaskSourceHashNames.SourceCapture capture)
        {
            List<IBuildTask> pipeline = new List<IBuildTask>
                {
                    new TaskPrepare_SBP(),
                    new TaskGetBuildMap_SBP(),
                    new TaskBuilding_SBP(),
                    new TaskVerifyBuildResult_SBP(),
                    new TaskEncryption_SBP(),
                    new TaskUpdateBundleInfo_SBP(),
                    new TaskCreateManifest_SBP(),
                    new TaskCreateReport_SBP()
                };
            if (capture != null)
                pipeline.Add(new TaskSourceHashNames(capture));
            pipeline.Add(new TaskCreatePackage_SBP());
            pipeline.Add(new TaskCopyBundledFiles_SBP());
            pipeline.Add(new TaskCreateCatalog_SBP());
            return pipeline;
        }
    }
}
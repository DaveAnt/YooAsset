using System;
using YooAsset;
using Xunit;

namespace YooAsset.SourceHash.Tests
{
    /// <summary>直接编译运行时命名实现，锁定旧枚举值和源名称与真实文件校验的分离。</summary>
    public sealed class BundleFileNamingTests
    {
        /// <summary>新增模式不得改变已有枚举值或默认值。</summary>
        [Fact]
        public void ExistingEnumValuesAndDefaultRemainUnchanged()
        {
            Assert.Equal(0, (int)EFileNameStyle.HashName);
            Assert.Equal(1, (int)EFileNameStyle.BundleName);
            Assert.Equal(2, (int)EFileNameStyle.BundleName_HashName);
            Assert.Equal(3, (int)EFileNameStyle.SourceHashName);
            Assert.Equal(EFileNameStyle.HashName, default(EFileNameStyle));
        }

        /// <summary>旧命名样式继续使用真实 fileHash；扩展名和无扩展名规则不变。</summary>
        [Theory]
        [InlineData(0, "assets_a.bundle", "md5.bundle")]
        [InlineData(0, "assets_a", "md5")]
        [InlineData(1, "assets_a.bundle", "assets_a.bundle")]
        [InlineData(1, "assets_a", "assets_a")]
        [InlineData(2, "assets_a.bundle", "assets_a_md5.bundle")]
        [InlineData(2, "assets_a", "assets_a_md5")]
        public void ExistingStylesKeepTheirNames(int style, string bundle, string expected)
        {
            Assert.Equal(expected, BundleFileNaming.GetBundleFileName(style, bundle, "md5"));
        }

        /// <summary>源样式只读取清单 BundleName，不把真实 MD5 混入源 SHA256 文件名。</summary>
        [Fact]
        public void SourceHashStyleUsesManifestBundleNameNotFileHash()
        {
            const string name = "d8f924aec7191e3732de601c479e1c2ba655ebfe65d8ca5f297aaa8c66738f72.bundle";
            Assert.Equal(name, BundleFileNaming.GetBundleFileName(3, name, "real-md5-first"));
            Assert.Equal(name, BundleFileNaming.GetBundleFileName(3, name, "real-md5-second"));
        }

        /// <summary>未知样式仍明确失败，不回退到源名或文件校验名。</summary>
        [Fact]
        public void UnknownStyleStillFails()
        {
            Assert.Throws<NotImplementedException>(() => BundleFileNaming.GetBundleFileName(4, "a.bundle", "md5"));
        }
    }
}

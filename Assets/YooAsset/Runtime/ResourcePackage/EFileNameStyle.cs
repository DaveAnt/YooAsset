
namespace YooAsset
{
    /// <summary>
    /// 远端资源文件命名风格
    /// </summary>
    public enum EFileNameStyle
    {
        /// <summary>
        /// 哈希值名称
        /// </summary>
        HashName = 0,

        /// <summary>
        /// 资源包名称（不推荐）
        /// </summary>
        BundleName = 1,

        /// <summary>
        /// 资源包名称 + 哈希值名称
        /// </summary>
        BundleName_HashName = 2,

        /// <summary>
        /// 原始源集合 SHA256 名称（仅支持可编程构建管线，不改变文件校验哈希）
        /// </summary>
        SourceHashName = 3,
    }
}
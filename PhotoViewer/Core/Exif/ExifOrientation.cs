namespace PhotoViewer.Core;

/// <summary>
/// 基于 EXIF Orientation 值的旋转与翻转辅助计算。
/// </summary>
public static class ExifOrientation
{
    /// <summary>
    /// 根据 EXIF Orientation 值计算旋转角度（仅处理旋转，不含翻转）。
    /// </summary>
    /// <param name="orientationValue">EXIF Orientation 值 (1~8)</param>
    /// <returns>旋转角度（0, 90, 180, 270）</returns>
    public static double GetRotationAngle(int orientationValue)
    {
        return orientationValue switch
        {
            1 => 0,    // 正常
            2 => 0,    // 水平翻转（暂不处理翻转，只处理旋转）
            3 => 180,  // 180度旋转
            4 => 180,  // 180度旋转+水平翻转
            5 => 90,   // 90度旋转+水平翻转
            6 => 90,   // 顺时针90度
            7 => 270,  // 270度旋转+水平翻转
            8 => 270,  // 逆时针90度（顺时针270度）
            _ => 0     // 未知值，不旋转
        };
    }

    /// <summary>
    /// 根据 EXIF Orientation 值判断是否需要水平翻转。
    /// </summary>
    /// <param name="orientationValue">EXIF Orientation 值 (1~8)</param>
    /// <returns>是否需要水平翻转</returns>
    public static bool NeedsHorizontalFlip(int orientationValue)
    {
        return orientationValue == 2 || orientationValue == 4 ||
               orientationValue == 5 || orientationValue == 7;
    }
}

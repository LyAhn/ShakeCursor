namespace ShakeCursor;

/// <summary>
/// The arrow shape shared by the cursor renderer and the settings preview, in "units"
/// (a classic arrow is roughly 14 wide x 24 tall). The tip sits at (0, 0) and
/// is the pointer hotspot.
/// </summary>
internal static class ArrowGeometry
{
    public static readonly (float X, float Y)[] Points =
    {
        (0.0f,  0.0f),
        (0.0f,  21.0f),
        (4.9f,  16.6f),
        (7.9f,  24.0f),
        (10.9f, 22.7f),
        (7.7f,  15.9f),
        (13.9f, 15.9f),
    };

    /// <summary>Base size in device-independent px per unit at scale 1.0.</summary>
    public const float UnitsToPx = 1.25f;
}

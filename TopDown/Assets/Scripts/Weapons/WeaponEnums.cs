/// <summary>
/// Defines which equipment slot a weapon occupies.
/// </summary>
public enum WeaponSlot
{
    Primary,
    Sidearm
}

/// <summary>
/// Defines how a weapon fires when the trigger is held.
/// </summary>
public enum FireMode
{
    /// <summary>One shot per trigger pull — must release and press again to fire.</summary>
    SemiAutomatic,

    /// <summary>Continuous fire while trigger is held.</summary>
    Automatic
}

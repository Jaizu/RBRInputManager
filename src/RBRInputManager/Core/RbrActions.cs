namespace RBRInputManager.Core;

public sealed record RbrAction(int Id, string Code, string Name, bool IsAnalog, string? InvertKey)
{
    /// <summary>What can be bound: steering and the combined pedal need an axis, pedals take either, the rest buttons/keys.</summary>
    public InputKind Accepts => Code is "STEERING" or "ACCELERATE_AND_BRAKE" ? InputKind.Axis
        : IsAnalog ? InputKind.Any : InputKind.Button;
}

/// <summary>
/// Action IDs as used in pf*.acm. The order comes from the CActionMap switch in
/// RichardBurnsRally_SSE.exe (jump table at VA 0x4C2AF0, 21 entries).
/// </summary>
public static class RbrActions
{
    public static readonly IReadOnlyList<RbrAction> All = new RbrAction[]
    {
        new(0,  "STEERING",             "Steering (axis)",              true,  "InvertSteeringAxis"),
        new(1,  "LEFT",                 "Steer left",                   false, null),
        new(2,  "RIGHT",                "Steer right",                  false, null),
        new(3,  "THROTTLE",             "Throttle",                     true,  "InvertThrottleAxis"),
        new(4,  "ACCELERATE_AND_BRAKE", "Throttle + brake (combined)",  true,  "InvertCombinedAxis"),
        new(5,  "BRAKE",                "Brake",                        true,  "InvertBrakeAxis"),
        new(6,  "HANDBRAKE",            "Handbrake",                    true,  "InvertHandbrakeAxis"),
        new(7,  "GEARUP",               "Gear up",                      false, null),
        new(8,  "GEARDWN",              "Gear down",                    false, null),
        new(9,  "CH_CAM",               "Change camera",                false, null),
        new(10, "PAUSE",                "Pause",                        false, null),
        new(11, "CLUTCH",               "Clutch",                       true,  "InvertClutchAxis"),
        new(12, "IGNITION",             "Ignition",                     false, null),
        new(13, "REVERSE",              "Reverse",                      false, null),
        new(14, "NEUTRAL",              "Neutral",                      false, null),
        new(15, "FIRSTGEAR",            "1st gear",                     false, null),
        new(16, "SECONDGEAR",           "2nd gear",                     false, null),
        new(17, "THIRDGEAR",            "3rd gear",                     false, null),
        new(18, "FOURTHGEAR",           "4th gear",                     false, null),
        new(19, "FIFTHGEAR",            "5th gear",                     false, null),
        new(20, "SIXTHGEAR",            "6th gear",                     false, null),
    };

    public static RbrAction Get(int id) =>
        id >= 0 && id < All.Count ? All[id] : new RbrAction(id, $"ACTION_{id}", $"Unknown action #{id}", false, null);
}

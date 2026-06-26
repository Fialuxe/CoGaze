/// <summary>
/// Shared input thresholds for Meta Touch (Plus) controllers.
///
/// The analog grip / PrimaryHandTrigger value at "fully squeezed" varies between
/// MQ3 and MQ3S (identical Touch Plus controllers, differing firmware), so a single
/// tuned threshold is used everywhere a grip press is detected. Centralised here so
/// the value is tuned in one place instead of being duplicated across MeshHandler,
/// SetupCoordinator and IdentificationTask.
/// </summary>
public static class OVRInputThresholds
{
    /// <summary>Analog grip value above which a grip counts as "pressed".</summary>
    public const float Grip = 0.7f;
}

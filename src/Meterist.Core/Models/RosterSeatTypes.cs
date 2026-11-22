namespace Meterist.Core.Models;

/// <summary>
/// Seat-type values on a roster that mean the person is <b>listed but holds no seat</b>, so they are not counted toward
/// the seats in use and are never reported as an idle seat. Gemini Enterprise's user licence list includes people with
/// no licence, among them those who tried to sign in and were refused (an unmet-demand signal worth reporting, not a seat).
/// Vendors whose member list is only seat holders (Claude, ChatGPT) never produce these values.
/// </summary>
public static class RosterSeatTypes
{
    /// <summary>A person who tried to sign in for the first time and had no licence to be given.</summary>
    public const string NoLicenseAttemptedLogin = "NO_LICENSE_ATTEMPTED_LOGIN";

    private static readonly HashSet<string> NoSeat = new(StringComparer.OrdinalIgnoreCase)
    {
        "NO_LICENSE",
        NoLicenseAttemptedLogin,
        "BLOCKED",
        "UNASSIGNED",
        "LICENSE_ASSIGNMENT_STATE_UNSPECIFIED",
    };

    /// <summary>True unless the seat type says the person has no licence or is blocked. A missing seat type is a seat holder.</summary>
    public static bool HoldsSeat(string? seatType) => string.IsNullOrWhiteSpace(seatType) || !NoSeat.Contains(seatType.Trim());

    public static bool TriedToSignInWithoutLicense(string? seatType) =>
        string.Equals(seatType?.Trim(), NoLicenseAttemptedLogin, StringComparison.OrdinalIgnoreCase);
}

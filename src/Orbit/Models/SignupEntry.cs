namespace Orbit.Models;

/// <summary>One line of the "Create accounts" list: Username:Password.</summary>
public sealed record SignupEntry(string Username, string Password);

/// <summary>Details that are the same for every account in one "Create accounts" run.</summary>
/// <param name="Gender">male | female | none</param>
public sealed record SignupOptions(DateTime Birthday, string Gender);

using System.Reflection;

namespace Pulse;

internal static class AppInfo
{
	/// <summary>The product version without the source revision suffix.</summary>
	public static string Version { get; } =
		typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
		?? "1.0.0";
}

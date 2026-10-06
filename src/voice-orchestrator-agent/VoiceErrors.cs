using System.Globalization;
using System.Resources;

namespace VoiceOrchestratorAgent;

internal static class VoiceErrors
{
    private static readonly ResourceManager s_resources = new("VoiceOrchestratorAgent.VoiceResources", typeof(VoiceErrors).Assembly);
    internal static string Get(string name) => s_resources.GetString(name, CultureInfo.CurrentUICulture)
        ?? throw new InvalidOperationException(name);
}

using System;
using System.Net.Http;

namespace Emby.Xtream.Plugin.Service
{
    /// <summary>
    /// Gives every outbound provider request a User-Agent.
    /// <para>
    /// <see cref="HttpClient"/> sends none unless one is set, and the provider-facing clients built
    /// here only set one when the user filled the field in. A request without a User-Agent is not
    /// an unusual request, and panels and the CDNs in front of them are free to answer it with a
    /// 404 instead of the JSON body: that is what #104 reported, a panel that answered curl with
    /// 200 and the plugin with 404 from the same container.
    /// </para>
    /// <para>
    /// A User-Agent configured by the user still wins and is sent unchanged, since a provider
    /// that requires one is exactly the case the field exists for.
    /// </para>
    /// </summary>
    internal static class XtreamUserAgent
    {
        /// <summary>Identifies the plugin when the user configured nothing.</summary>
        internal static readonly string Default = BuildDefault();

        /// <summary>
        /// Sets the User-Agent on <paramref name="client"/> to the configured value, or to
        /// <see cref="Default"/> when there is none. Replaces any existing value, so calling it
        /// again after the user clears the field restores the default rather than leaving the old
        /// header behind.
        /// </summary>
        internal static void Apply(HttpClient client, string configuredUserAgent)
        {
            if (client == null)
                return;

            client.DefaultRequestHeaders.Remove("User-Agent");

            var value = string.IsNullOrWhiteSpace(configuredUserAgent)
                ? Default
                : configuredUserAgent.Trim();

            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", value);
        }

        private static string BuildDefault()
        {
            var version = typeof(XtreamUserAgent).Assembly.GetName().Version;
            return "Emby-Xtream-Plugin/" + (version == null ? "1.0" : version.ToString(3));
        }
    }
}
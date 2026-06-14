using System.Text;

namespace Topaz.Deploy;

// Renders the docker-compose env-file (stack.env) as raw UTF-8 bytes.
//
// Why a dedicated builder: the previous PowerShell deploy mangled bodies
// that contained `$`, quotes, or newlines because the remote shell re-parsed
// the string when ssh piped it in. The new SFTP path writes bytes byte-for-
// byte over the wire, so the only place mangling could still happen is here.
// Isolating the byte production makes it trivially testable in isolation.
public static class StackEnvBuilder
{
    public static byte[] Build(string connectorToken, string topazVersion, string portalVersion)
    {
        var sb = new StringBuilder();
        sb.Append("TUNNEL_TOKEN=").Append(connectorToken).Append('\n');
        // Host and portal are versioned independently but released together;
        // compose reads these as ${TOPAZ_VERSION}/${PORTAL_VERSION}.
        sb.Append("TOPAZ_VERSION=").Append(topazVersion).Append('\n');
        sb.Append("PORTAL_VERSION=").Append(portalVersion).Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}

namespace SqlHelper.App.Services;

/// <summary>
/// Turns the handful of SQL Server connection failures an operator actually hits into a
/// sentence that says what to change, instead of leaving them to decode the raw provider text.
/// </summary>
public static class ConnectionErrorHelp
{
    public static string Explain(string? rawError)
    {
        if (string.IsNullOrWhiteSpace(rawError))
        {
            return "The connection failed, but the server didn't say why.";
        }

        string hint = Hint(rawError);
        return hint.Length == 0 ? rawError : $"{hint}\n\nServer said: {rawError}";
    }

    private static string Hint(string error)
    {
        if (Contains(error, "certificate chain was issued by an authority that is not trusted")
            || Contains(error, "The target principal name is incorrect")
            || Contains(error, "certificate verify failed"))
        {
            return "The server is using a certificate this PC doesn't trust — normal for an internal SQL Server with a "
                 + "self-signed certificate. If you know this server, tick \"Trust the server certificate\" below and test again. "
                 + "The better long-term fix is a proper certificate on the server.";
        }

        if (Contains(error, "Login failed for user"))
        {
            return "The server was reached, but it rejected the sign-in. Check the user name and password, and that this "
                 + "login is allowed on this database.";
        }

        if (Contains(error, "Cannot open database"))
        {
            return "The server was reached and the login worked, but that database name doesn't exist there — or this login "
                 + "has no access to it.";
        }

        if (Contains(error, "network-related or instance-specific")
            || Contains(error, "error: 26")
            || Contains(error, "server was not found"))
        {
            return "Couldn't reach the server at all. Check the server name (include the instance, e.g. SQL-01\\SQLEXPRESS), "
                 + "that SQL Server is running, and that TCP/IP is enabled and not blocked by a firewall.";
        }

        if (Contains(error, "timeout") || Contains(error, "timed out"))
        {
            return "The server didn't answer in time. It may be unreachable from here, or busy — try again, or raise the "
                 + "connect timeout for this database.";
        }

        if (Contains(error, "integrated security") || Contains(error, "SSPI"))
        {
            return "Windows authentication failed. Your Windows account may not have a login on that server — try a SQL "
                 + "Server login instead.";
        }

        return string.Empty;
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}

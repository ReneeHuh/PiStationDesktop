using PiStation.PiRpc.Discovery;

namespace PiStation.Host;

public static class PiSetupCommand
{
    public static (string Command, string Instructions) Create(PiInstallation installation, string action)
    {
        var (arguments, instructions) = action switch
        {
            "login" => (new[] { "--no-session", "--no-extensions", "--no-skills", "--no-prompt-templates", "--no-context-files" },
                "In the Pi terminal, enter /login and follow the provider flow. Use /logout to remove a saved account. Exit Pi, then restart the thread and refresh this panel."),
            "resources" => (new[] { "config" },
                "Pi config manages package and local resources. Tab switches user/project scope. Close it, restart the thread, then refresh this panel."),
            "packages" => (new[] { "list" },
                "This terminal lists Pi packages. Use pi install <source>, pi remove <source>, or pi update as needed, then restart the thread."),
            _ => throw new ArgumentException("Choose login, resource configuration, or package management."),
        };
        var invocation = "& " + Quote(installation.ExecutablePath) + " " + string.Join(" ", installation.LaunchPrefixArguments.Select(Quote));
        return ("function global:pi { " + invocation + " @args }; pi " + string.Join(" ", arguments.Select(Quote)), instructions);
    }

    private static string Quote(string value)
    {
        if (value.Any(char.IsControl)) throw new ArgumentException("A Pi command argument contains a control character.");
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}

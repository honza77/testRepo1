using System;
using System.IO;
using System.Security;

namespace MsiRemoteUninstall
{
    /// <summary>User name + password. null credential = current Windows logon.</summary>
    public sealed class Credential
    {
        public Credential(string userName, SecureString password)
        {
            UserName = userName;
            Password = password;
        }

        public string UserName { get; }
        public SecureString Password { get; }
    }

    public static class ConsoleCredentials
    {
        private static SecureString ToSecure(string s)
        {
            var ss = new SecureString();
            foreach (var c in s) ss.AppendChar(c);
            ss.MakeReadOnly();
            return ss;
        }

        /// <summary>Masked console password input ('*'); plain line read when stdin is redirected.</summary>
        public static SecureString ReadPassword(string prompt)
        {
            Console.Write(prompt + ": ");
            if (Console.IsInputRedirected)
            {
                var line = Console.In.ReadLine();
                Console.WriteLine();
                return ToSecure(line ?? "");
            }
            var pwd = new SecureString();
            while (true)
            {
                var k = Console.ReadKey(true);
                if (k.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
                if (k.Key == ConsoleKey.Backspace)
                {
                    if (pwd.Length > 0) { pwd.RemoveAt(pwd.Length - 1); Console.Write("\b \b"); }
                }
                else if (k.Key == ConsoleKey.Escape)
                {
                    while (pwd.Length > 0) { pwd.RemoveAt(pwd.Length - 1); Console.Write("\b \b"); }
                }
                else if (k.KeyChar != '\0' && !char.IsControl(k.KeyChar))
                {
                    pwd.AppendChar(k.KeyChar);
                    Console.Write('*');
                }
            }
            pwd.MakeReadOnly();
            return pwd;
        }

        /// <summary>Current user (null), environment variable, or console prompt. Raises MRU201-204.</summary>
        public static Credential Obtain(string userName, string passwordEnvVar, bool currentUser)
        {
            if (currentUser) return null;
            try
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("Enter credentials with local admin rights on the target servers");
                Console.ResetColor();
                var user = userName;
                if (!string.IsNullOrEmpty(user))
                    Console.WriteLine("User name: " + user);
                else
                {
                    Console.Write(@"User name (e.g. DOMAIN\user): ");
                    user = (Console.ReadLine() ?? "").Trim();
                }
                if (user.Length == 0) throw new MruException("MRU201", "The user name was empty.");

                SecureString password;
                if (passwordEnvVar != null)
                {
                    var v = Environment.GetEnvironmentVariable(passwordEnvVar);
                    if (string.IsNullOrEmpty(v)) throw new MruException("MRU203", $"Environment variable '{passwordEnvVar}' is not set or empty.");
                    password = ToSecure(v);
                }
                else
                    password = ReadPassword($"Password for {user}");
                if (password.Length == 0) throw new MruException("MRU202", "The password was empty.");
                return new Credential(user, password);
            }
            catch (InvalidOperationException ex) { throw new MruException("MRU204", ex.Message); }
            catch (IOException ex) { throw new MruException("MRU204", ex.Message); }
        }
    }
}

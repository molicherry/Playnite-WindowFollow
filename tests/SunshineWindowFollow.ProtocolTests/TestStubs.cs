using System;
using System.Collections.Generic;

// The protocol tests link the plugin's pure protocol sources without Playnite.
// These stubs provide only the surface those sources consume.
namespace Playnite.SDK
{
    public interface ILogger
    {
        void Debug(string message);

        void Info(string message);

        void Warn(string message);

        void Error(string message);

        void Error(Exception exception, string message);
    }
}

namespace SunshineWindowFollow
{
    public class SunshineSettings
    {
        public string ServerUrl { get; set; } = "https://127.0.0.1:47990";

        public string Username { get; set; } = string.Empty;

        public string Password { get; set; } = string.Empty;
    }
}

namespace SunshineWindowFollow.ProtocolTests
{
    internal sealed class RecordingLogger : Playnite.SDK.ILogger
    {
        public List<string> Messages { get; } = new List<string>();

        public void Debug(string message)
        {
            Messages.Add("DEBUG " + message);
        }

        public void Info(string message)
        {
            Messages.Add("INFO " + message);
        }

        public void Warn(string message)
        {
            Messages.Add("WARN " + message);
        }

        public void Error(string message)
        {
            Messages.Add("ERROR " + message);
        }

        public void Error(Exception exception, string message)
        {
            Messages.Add("ERROR " + message + ": " + exception);
        }
    }
}

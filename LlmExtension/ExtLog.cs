// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LlmExtension;

/// <summary>
///     File logger for debugging the extension in place. Verbose messages are
///     only written when <see cref="Enabled"/> is set (config "Debug": true);
///     errors are always written so failures can be diagnosed after the fact.
///     The file lives next to config.json and is kept bounded (1 MB).
/// </summary>
internal static class ExtLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config",
        "LlmExtensionForCmdPal",
        "ext-debug.log");

    private static readonly object Gate = new();

    public static bool Enabled { get; set; }

    public static void Log(string message)
    {
        if (!Enabled)
        {
            return;
        }

        Append(message);
    }

    /// <summary>
    ///     Always logged, regardless of the Debug flag.
    /// </summary>
    public static void LogError(string message)
    {
        Append(message);
    }

    private static void Append(string message)
    {
        try
        {
            lock (Gate)
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (!File.Exists(LogPath))
                {
                    File.WriteAllText(LogPath, string.Empty);
                }
                else if (new FileInfo(LogPath).Length > 1_000_000)
                {
                    // Keep the log bounded: start over when it grows past 1 MB.
                    File.WriteAllText(LogPath, string.Empty);
                }

                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the extension.
        }
    }
}

/// <summary>
///     Coalesces "list changed" notifications into occasional raises on a
///     worker thread. The first Schedule() starts one short delayed raise;
///     changes made while a raise is pending are picked up by that same raise.
///
///     This matters for two reasons:
///     - raising ItemsChanged from inside the host's SearchText setter RPC
///       forces the host into a nested GetItems RPC on the same exclusive
///       thread while the setter call is still in flight, which stalls the
///       pipeline when it happens on every keystroke (typing "/settings");
///     - raising on every streamed token floods the host with a full item
///       re-fetch per token, which makes the list thrash instead of streaming.
/// </summary>
internal sealed class ItemsChangedThrottle
{
    private readonly Action _raise;
    private readonly int _delayMs;
    private int _pending;

    public ItemsChangedThrottle(Action raise, int delayMs = 90)
    {
        _raise = raise;
        _delayMs = delayMs;
    }

    public void Schedule()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1)
        {
            // A raise is already scheduled; it will read the latest state.
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_delayMs);
            }
            catch
            {
                Volatile.Write(ref _pending, 0);
                return;
            }

            Volatile.Write(ref _pending, 0);

            try
            {
                _raise();
            }
            catch
            {
                // Never let a raise take the extension down.
            }
        });
    }
}

/// <summary>
///     Small text helpers for list items.
/// </summary>
internal static class TextUtils
{
    /// <summary>
    ///     Collapses a response to a single line, keeping the tail (the part
    ///     that is growing while streaming), for use in compact subtitles.
    /// </summary>
    public static string OneLine(string text, int max = 110)
    {
        var single = text.Replace("\r", " ").Replace("\n", " ");
        while (single.Contains("  ", StringComparison.Ordinal))
        {
            single = single.Replace("  ", " ", StringComparison.Ordinal);
        }

        single = single.TrimStart();

        return single.Length <= max
            ? single
            : "..." + single[^max..];
    }
}

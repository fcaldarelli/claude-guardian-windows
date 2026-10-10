using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeGuardian
{
    /// <summary>
    /// Derives a session's title from its JSONL transcript.
    ///
    /// Priority:
    ///  1. name set with /rename (or --name)
    ///  2. title generated automatically by Claude Code
    ///  3. first message written by the user
    ///
    /// The transcript format is not a documented API and may change between versions:
    /// that is why the parser is lenient (it accepts several field names) and, if it finds
    /// no title, falls back to the first prompt.
    ///
    /// The file is read incrementally: each call reads only
    /// the bytes added since the last time.
    /// </summary>
    public sealed class TranscriptTitleReader
    {
        private sealed class State
        {
            public long Offset;
            public byte[] Remainder = new byte[0];
            public string CustomTitle;
            public string AutoTitle;
            public string FirstPrompt;
            /// <summary>
            /// The last transcript event is a user interruption or an API error:
            /// in these cases Claude Code does not invoke the Stop hook.
            /// </summary>
            public bool Interrupted;
            public DateTime? LastModified;

            public string Title => CustomTitle ?? AutoTitle ?? FirstPrompt;
        }

        public sealed class Info
        {
            public string Title;
            public bool Interrupted;
            public DateTime? LastModified;
        }

        private readonly Dictionary<string, State> _states = new Dictionary<string, State>(StringComparer.OrdinalIgnoreCase);
        private const int MaxLength = 60;

        public Info InfoFor(string path)
        {
            if (!_states.TryGetValue(path, out var state))
            {
                state = new State();
                _states[path] = state;
            }
            Update(path, ref state);
            _states[path] = state;
            return new Info { Title = state.Title, Interrupted = state.Interrupted, LastModified = state.LastModified };
        }

        /// <summary>Frees the memory of transcripts for sessions that no longer exist.</summary>
        public void Retain(ISet<string> paths)
        {
            foreach (var key in _states.Keys.Where(k => !paths.Contains(k)).ToList())
                _states.Remove(key);
        }

        private void Update(string path, ref State state)
        {
            try
            {
                // Full sharing: Claude Code must be able to keep writing (and recreating) the file.
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    // From the handle and not from the directory entry, which NTFS updates late
                    // while the file is open for writing.
                    if (GetFileTime(stream.SafeFileHandle, IntPtr.Zero, IntPtr.Zero, out var written))
                        state.LastModified = DateTime.FromFileTimeUtc(written);

                    var end = stream.Length;
                    if (end < state.Offset)                         // file recreated or truncated
                    {
                        state = new State { LastModified = state.LastModified };
                    }
                    if (end <= state.Offset) return;

                    stream.Seek(state.Offset, SeekOrigin.Begin);
                    var chunk = new byte[end - state.Offset];
                    var read = 0;
                    while (read < chunk.Length)
                    {
                        var n = stream.Read(chunk, read, chunk.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    state.Offset += read;

                    var data = new byte[state.Remainder.Length + read];
                    Buffer.BlockCopy(state.Remainder, 0, data, 0, state.Remainder.Length);
                    Buffer.BlockCopy(chunk, 0, data, state.Remainder.Length, read);

                    // The last line may be incomplete (Claude Code is still writing): we keep it aside.
                    var start = 0;
                    for (var i = 0; i < data.Length; i++)
                    {
                        if (data[i] != (byte)'\n') continue;
                        if (i > start) Process(Encoding.UTF8.GetString(data, start, i - start), state);
                        start = i + 1;
                    }
                    state.Remainder = new byte[data.Length - start];
                    Buffer.BlockCopy(data, start, state.Remainder, 0, state.Remainder.Length);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // MARK: - Parsing

        private void Process(string text, State state)
        {
            // Turn state: an interruption or API error ends the turn without a Stop hook;
            // any subsequent message (new prompt, response, tool) reopens it.
            if (text.Contains("\"isApiErrorMessage\":true"))
            {
                state.Interrupted = true;
                return;
            }
            if (text.Contains("[Request interrupted by user") && IsInterruptMarker(text))
            {
                state.Interrupted = true;
                return;
            }
            if (text.Contains("\"type\":\"assistant\"") || text.Contains("\"type\":\"user\""))
            {
                state.Interrupted = false;
            }

            // Quick filter before JSON parsing: most lines are of no interest to us.
            var maybeTitle = text.Contains("itle\"") || text.Contains("\"summary\"");
            var maybePrompt = state.FirstPrompt == null && text.Contains("\"type\":\"user\"");
            if (!maybeTitle && !maybePrompt) return;

            var obj = Json.TryParseObject(text);
            var type = obj.GetString("type");
            if (type == null) return;

            if (type == "user")
            {
                if (state.FirstPrompt == null)
                {
                    var prompt = UserPrompt(obj);
                    if (prompt != null) state.FirstPrompt = prompt;
                }
                return;
            }

            var lowered = type.ToLowerInvariant();
            if (lowered != "summary" && !lowered.Contains("title")) return;

            var keys = new[] { "customTitle", "custom_title", "title", "aiTitle", "ai_title", "summary" };
            var raw = keys.Select(k => obj.TryGetValue(k, out var v) ? v as string : null).FirstOrDefault(v => v != null);
            var value = Clean(raw);
            if (value == null) return;

            if (lowered.Contains("custom") || obj.ContainsKey("customTitle"))
                state.CustomTitle = value;       // the last /rename wins
            else
                state.AutoTitle = value;
        }

        /// <summary>
        /// Checks that the line really is the interruption message written by Claude Code
        /// and not, for example, a prompt quoting that text.
        /// </summary>
        private static bool IsInterruptMarker(string line)
        {
            var obj = Json.TryParseObject(line);
            if (obj.GetString("type") != "user") return false;
            return MessageTexts(obj).Any(t => t.TrimStart().StartsWith("[Request interrupted by user", StringComparison.Ordinal));
        }

        private static List<string> MessageTexts(Dictionary<string, object> obj)
        {
            var texts = new List<string>();
            if (!(obj.TryGetValue("message", out var m) && m is Dictionary<string, object> message)) return texts;
            if (!message.TryGetValue("content", out var content)) return texts;
            if (content is string s)
            {
                texts.Add(s);
            }
            else if (content is List<object> blocks)
            {
                foreach (var block in blocks.OfType<Dictionary<string, object>>())
                {
                    var t = block.GetString("text");
                    if (t != null) texts.Add(t);
                }
            }
            return texts;
        }

        private string UserPrompt(Dictionary<string, object> obj)
        {
            if (obj.GetBool("isMeta")) return null;
            var userType = obj.GetString("userType");
            if (userType != null && userType != "external") return null;
            if (!(obj.TryGetValue("message", out var m) && m is Dictionary<string, object> message)) return null;

            string text = null;
            if (message.TryGetValue("content", out var content))
            {
                if (content is string s)
                {
                    text = s;
                }
                else if (content is List<object> blocks)
                {
                    // Tool responses (tool_result) are not user prompts.
                    text = blocks.OfType<Dictionary<string, object>>()
                        .FirstOrDefault(b => b.GetString("type") == "text")
                        .GetString("text");
                }
            }

            var cleaned = Clean(text);
            if (cleaned == null) return null;
            // Messages generated by commands or by the system, e.g. "<command-name>/clear</command-name>".
            if (cleaned.StartsWith("<", StringComparison.Ordinal) || cleaned.StartsWith("Caveat:", StringComparison.Ordinal)) return null;
            return cleaned;
        }

        /// <summary>First line, whitespace collapsed, truncated to a length readable in a menu.</summary>
        private static string Clean(string raw)
        {
            if (raw == null) return null;
            var firstLine = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            var compact = string.Join(" ", firstLine.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            if (compact.Length == 0) return null;
            if (compact.Length <= MaxLength) return compact;
            return compact.Substring(0, MaxLength - 1) + "…";
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileTime(Microsoft.Win32.SafeHandles.SafeFileHandle file, IntPtr creation, IntPtr access, out long write);
    }
}

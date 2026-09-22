using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace VsAgentic.Services.ClaudeCli.Permissions;

/// <summary>
/// Turns a pending permission request into the rules the CLI needs in order to
/// stop asking for calls of the same shape.
///
/// Rules are written in the CLI's own syntax, where <c>Bash(git log:*)</c> means
/// "any command starting with git log", <c>Bash(git -C . status)</c> means "that
/// command and nothing else" and <c>Edit(//c/src/a.cs)</c> means "edits to that
/// one file". The CLI does the matching; this class only decides what to key on.
///
/// Everything here fails the same way. A rule is granted without being asked
/// again, so it must never cover a command the user was not shown. Wherever the
/// line cannot be read with certainty, no rules are produced at all: the banner
/// then drops the button and the next call of the same shape is asked again,
/// which costs a click and grants nothing.
/// </summary>
public static class PermissionRuleBuilder
{
    private static readonly IReadOnlyList<PermissionRule> None = Array.Empty<PermissionRule>();

    /// <summary>
    /// Rules covering calls of this shape. Empty when the request has nothing
    /// stable to key on, in which case only a one-off allow makes sense.
    /// </summary>
    public static IReadOnlyList<PermissionRule> Build(string toolName, JsonElement input)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return None;

        if (IsShell(toolName))
            return BuildShellRules(toolName, input);

        if (IsFileEdit(toolName))
        {
            // The CLI matches every file-editing tool against Edit rules, so a
            // Write(...) rule would be stored and then never consulted.
            var path = FileRulePath(ReadString(input, "file_path") ?? ReadString(input, "notebook_path"));
            return path is null
                ? None
                : new[] { new PermissionRule("Edit", path) };
        }

        if (toolName.Equals("Read", StringComparison.OrdinalIgnoreCase))
        {
            var path = FileRulePath(ReadString(input, "file_path"));
            return path is null
                ? None
                : new[] { new PermissionRule("Read", path) };
        }

        if (toolName.Equals("WebFetch", StringComparison.OrdinalIgnoreCase))
        {
            // Without a specifier this would allow fetching any URL.
            return Uri.TryCreate(ReadString(input, "url"), UriKind.Absolute, out var uri) && uri.Host.Length > 0
                ? new[] { new PermissionRule(toolName, "domain:" + uri.Host) }
                : None;
        }

        // A rule for a whole tool is only as narrow as the tool itself, so it is
        // kept to the ones whose arguments cannot widen it: an MCP tool reaches
        // exactly one server, and WebSearch reads a search engine. Grep and Glob
        // are not in that group — without a path they search the whole disk,
        // while Read above is held to one file.
        return IsMcpTool(toolName) || toolName.Equals("WebSearch", StringComparison.OrdinalIgnoreCase)
            ? new[] { new PermissionRule(toolName) }
            : None;
    }

    private static IReadOnlyList<PermissionRule> BuildShellRules(string toolName, JsonElement input)
    {
        var command = ReadString(input, "command");
        if (string.IsNullOrWhiteSpace(command)) return None;

        // Bash escapes with a backslash and substitutes with a backtick;
        // PowerShell escapes with the backtick and leaves the backslash alone,
        // which matters on Windows paths.
        var posix = toolName.Equals("Bash", StringComparison.OrdinalIgnoreCase);

        // A shell line is usually several commands joined by && or |, and the
        // CLI only stops asking once every one of them is covered. Granting the
        // first segment alone produces Bash(cd:*) for "cd x && rm -rf y", which
        // reads as if the prompt was about cd while the next prompt is
        // identical.
        var segments = SplitCommands(command!, posix);
        if (segments is null || segments.Count == 0) return None;

        var rules = new List<PermissionRule>();

        foreach (var segment in segments)
        {
            var content = CommandRule(segment, posix);

            // One command that cannot be read is enough to drop the button.
            // Keeping the other rules would not help: the CLI would go on asking
            // for this line anyway, so the click would buy nothing while the
            // banner claimed it bought something.
            if (content is null) return None;

            var rule = new PermissionRule(toolName, content);
            if (!rules.Any(r => r.Display == rule.Display))
                rules.Add(rule);
        }

        return rules;
    }

    private static bool IsShell(string toolName) =>
        toolName.Equals("Bash", StringComparison.OrdinalIgnoreCase) ||
        toolName.Equals("PowerShell", StringComparison.OrdinalIgnoreCase);

    private static bool IsFileEdit(string toolName) =>
        toolName.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
        toolName.Equals("MultiEdit", StringComparison.OrdinalIgnoreCase) ||
        toolName.Equals("Write", StringComparison.OrdinalIgnoreCase) ||
        toolName.Equals("NotebookEdit", StringComparison.OrdinalIgnoreCase);

    private static bool IsMcpTool(string toolName) =>
        toolName.StartsWith("mcp__", StringComparison.Ordinal);

    /// <summary>
    /// The commands a shell line runs, or null when the line holds syntax this
    /// class cannot read the same way the shell does.
    ///
    /// Splitting is the whole risk in this class. Every character below either
    /// ends a command, or means that what follows is not what it looks like —
    /// and a rule built on a misread line names one command while allowing
    /// another. So anything that hides a command (a heredoc body, a
    /// substitution, a subshell), and anything that takes the meaning off a
    /// quote or a separator (an escape), gives up on the whole line instead.
    /// </summary>
    private static List<string>? SplitCommands(string command, bool posix)
    {
        var escape = posix ? '\\' : '`';
        var commands = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        var wordStart = true;

        void Flush()
        {
            var s = current.ToString().Trim();
            if (s.Length > 0) commands.Add(s);
            current.Clear();
        }

        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            var next = i + 1 < command.Length ? command[i + 1] : '\0';
            var previous = i > 0 ? command[i - 1] : '\0';

            if (quote != '\0')
            {
                // Inside double quotes the escape bites on these four, and every
                // one of them moves the end of the string: echo "a\" | sh "
                // prints one string, but read without the escape it looks like a
                // pipe into sh.
                if (c == escape && quote == '"' &&
                    (next == '"' || next == escape || next == '$' || next == '`' || next == '\n'))
                    return null;

                if (c == quote)
                {
                    // PowerShell doubles a quote to escape it, so 'a''b' is one
                    // string. Read as two strings, whatever sits between them
                    // would be taken for shell syntax.
                    if (!posix && next == quote) return null;
                    quote = '\0';
                }

                current.Append(c);
                continue;
            }

            if (c == '"' || c == '\'')
            {
                quote = c;
                current.Append(c);
                wordStart = false;
                continue;
            }

            if (c == escape)
            {
                // Outside quotes the escape takes the meaning off the next
                // character, which is exactly the meaning this loop reads: in
                // echo a\;b the semicolon is text, and a line ending in the
                // escape continues on the next one. An escaped ordinary
                // character (R:\3) changes nothing here and is kept.
                if (next == '\0' || next == '"' || next == '\'' || next == ';' || next == '|' ||
                    next == '&' || next == '\n' || next == '\r' || next == '$' || next == '`' ||
                    next == escape)
                    return null;

                current.Append(c);
                wordStart = false;
                continue;
            }

            // A substitution or a subshell puts a whole command where this loop
            // sees an argument, and brace expansion multiplies the line.
            if (posix && c == '`') return null;
            if (c == '$' && (next == '(' || next == '{')) return null;
            if (c == '(' || c == ')' || c == '{' || c == '}') return null;

            // A heredoc turns the rest of the line and the lines after it into
            // text. The body is usually a file being written, and reading it as
            // commands is how a curl … | sh inside a deploy script became a
            // rule of its own.
            if (c == '<' && next == '<') return null;

            if (c == '#' && wordStart)
            {
                // The rest of the line is a comment: ls # ; rm -rf x runs ls and
                // nothing else.
                Flush();
                while (i + 1 < command.Length && command[i + 1] != '\n') i++;
                continue;
            }

            if (c == '&')
            {
                if (next == '&')
                {
                    Flush();
                    i++;
                    wordStart = true;
                    continue;
                }

                // 2>&1 and &>out are redirections, not the background operator.
                if (previous == '>' || next == '>')
                {
                    current.Append(c);
                    wordStart = false;
                    continue;
                }

                // A lone & sends the command to the background and starts
                // another one, so the tail of the line is not an argument of
                // this one. PowerShell's & is the call operator, which is no
                // more readable.
                return null;
            }

            if (c == ';' || c == '|' || c == '\n' || c == '\r')
            {
                Flush();
                if (c == '|' && next == '|') i++;
                wordStart = true;
                continue;
            }

            current.Append(c);
            wordStart = c == ' ' || c == '\t';
        }

        // An unbalanced quote means the split above ran on a reading of the line
        // the shell does not share.
        if (quote != '\0') return null;

        Flush();
        return commands;
    }

    /// <summary>
    /// The rule content for one command, or null when the command gives nothing
    /// that can be keyed on without covering more than it says.
    /// </summary>
    private static string? CommandRule(string segment, bool posix)
    {
        var words = SplitWords(segment);
        if (words.Count == 0) return null;

        // A command that sends its output into a file is refused by the CLI
        // whatever the rules say — checked against 2.1.270 with a rule holding
        // the command verbatim. No rule helps here, so the button would promise
        // something it cannot deliver. 2>&1 and >&2 point one handle at another
        // and write nothing, so they do not count.
        if (WritesToFile(segment)) return null;

        var head = words[0];

        // Shell syntax, not a command. Bash(for:*), Bash(then:*) and Bash(fi:*)
        // were all found in settings after a day of use: they allow nothing,
        // name nothing, and only take up room where the user looks for the rules
        // that do something.
        if (ShellKeywords.Contains(head)) return null;

        // VAR=value in front of a command. A rule that dropped the assignment
        // would not match the line it came from, and one that kept it would key
        // on a value instead of a command — Bash(R="R:/3:*) is the real example.
        if (head.IndexOf('=') >= 0) return null;

        var quoted = QuotedProgram(head);
        if (quoted is not null)
        {
            // A program named by full path is specific on its own, so no
            // subcommand is collected. The quotes stay in the rule, because the
            // CLI compares against the command as written.
            var leaf = ExecutableLeaf(quoted.Substring(1, quoted.Length - 2));
            return ExactRuleOnly.Contains(leaf) ? ExactRule(segment) : quoted + ":*";
        }

        if (!IsCommandName(head)) return null;

        // Read-only commands take no subcommand: cd src would otherwise yield
        // Bash(cd src:*), which never matches the next cd. The list is held to
        // commands that only read, so the widest thing a rule from it can allow
        // is a read.
        if (ReadOnlyCommands.Contains(head)) return head + ":*";

        // Some commands never get a prefix rule at all. See the list.
        if (ExactRuleOnly.Contains(head)) return ExactRule(segment);

        // git log --oneline -5 keys on "git log". One subcommand, not two: two
        // looks right on gh issue list but misreads git ls-tree main, where the
        // second word is a branch name and the rule would only ever match that
        // branch. A flag, a path or a quoted string in this position is an
        // operand, and generalising over it is what turned git -C . status into
        // Bash(git:*).
        if (words.Count >= 2 && IsPlainWord(words[1]))
            return head + " " + words[1] + ":*";

        // Nothing here can be generalised from, so the rule is the command as
        // written. It matches the same call again and nothing else.
        return ExactRule(segment);
    }

    /// <summary>
    /// The command as written, which the CLI matches exactly. Null when the text
    /// would not mean the same thing next time: a rule ending in <c>:*</c> reads
    /// as a prefix, a star or a question mark is a wildcard to the CLI's matcher
    /// (<c>Bash(touch x*)</c> was checked against 2.1.270 and allows
    /// <c>touch xyz</c>), and a variable means whatever it holds at the time it
    /// is matched.
    /// </summary>
    private static string? ExactRule(string segment)
    {
        var text = segment.Trim();
        if (text.Length == 0) return null;
        if (text.IndexOfAny(new[] { '$', '*', '?' }) >= 0) return null;
        if (text.EndsWith(":*", StringComparison.Ordinal)) return null;
        return text;
    }

    /// <summary>
    /// The words of a command, with quoted strings kept whole: the program in
    /// <c>"C:/Program Files/gh/gh.exe" --version</c> is one word. Splitting on
    /// spaces first gave a rule for "C:/Program", which matches nothing.
    /// </summary>
    private static List<string> SplitWords(string segment)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';

        foreach (var c in segment)
        {
            if (quote != '\0')
            {
                current.Append(c);
                if (c == quote) quote = '\0';
                continue;
            }

            if (c == '"' || c == '\'')
            {
                quote = c;
                current.Append(c);
                continue;
            }

            if (c == ' ' || c == '\t')
            {
                if (current.Length > 0)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0) words.Add(current.ToString());
        return words;
    }

    /// <summary>
    /// A bare command name: <c>dotnet</c>, <c>python3</c>, <c>g++</c>,
    /// <c>Get-ChildItem</c>. A word holding a path separator, an operator, a
    /// variable or a quote is not one, and nothing is generalised from it: the
    /// rule would carry a piece of syntax this class did not read.
    /// </summary>
    private static bool IsCommandName(string word)
    {
        if (word.Length == 0) return false;
        if (!char.IsLetterOrDigit(word[0]) && word[0] != '_') return false;

        foreach (var c in word)
        {
            if (char.IsLetterOrDigit(c)) continue;
            if (c != '_' && c != '-' && c != '.' && c != '+') return false;
        }

        return true;
    }

    /// <summary>
    /// A subcommand: a bare word such as <c>status</c>, <c>log</c> or
    /// <c>build</c>. Nothing with a dot, a slash or a colon in it, so a version,
    /// a path or a URL never ends up in a prefix rule, and nothing starting with
    /// a digit, because a number is an operand — <c>kill 123</c> would otherwise
    /// give a rule that also covers <c>kill 1234</c>.
    /// </summary>
    private static bool IsPlainWord(string word)
    {
        if (word.Length == 0) return false;
        if (!char.IsLetter(word[0]) && word[0] != '_') return false;

        foreach (var c in word)
        {
            if (char.IsLetterOrDigit(c)) continue;
            if (c != '_' && c != '-') return false;
        }

        return true;
    }

    /// <summary>
    /// The first word when it is one whole quoted string, quotes included, or
    /// null when it is not.
    /// </summary>
    private static string? QuotedProgram(string word)
    {
        if (word.Length < 3) return null;

        var quote = word[0];
        if (quote != '"' && quote != '\'') return null;
        if (word[word.Length - 1] != quote) return null;

        // One string, not two run together: "a""b" is not a program path.
        return word.IndexOf(quote, 1) == word.Length - 1 ? word : null;
    }

    /// <summary>The file name of a program path, without directory or extension.</summary>
    private static string ExecutableLeaf(string path)
    {
        var p = path.Replace('\\', '/');

        var slash = p.LastIndexOf('/');
        if (slash >= 0) p = p.Substring(slash + 1);

        var dot = p.LastIndexOf('.');
        return dot > 0 ? p.Substring(0, dot) : p;
    }

    /// <summary>
    /// Whether the command sends its output into a file. <c>2&gt;&amp;1</c> and
    /// <c>&gt;&amp;2</c> point one handle at another and write nothing, so they
    /// do not count: the compound line in the issue ends in
    /// <c>git push … 2&gt;&amp;1 | tail -5</c>.
    /// </summary>
    private static bool WritesToFile(string segment)
    {
        var quote = '\0';

        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];

            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }

            if (c == '"' || c == '\'')
            {
                quote = c;
                continue;
            }

            if (c != '>') continue;

            var j = i + 1;
            while (j < segment.Length && (segment[j] == '>' || segment[j] == ' ' || segment[j] == '\t')) j++;
            if (j >= segment.Length || segment[j] != '&') return true;
        }

        return false;
    }

    /// <summary>
    /// Commands that only read, and that take an operand where another command
    /// would take a subcommand.
    /// </summary>
    private static readonly HashSet<string> ReadOnlyCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "cd", "ls", "dir", "pwd", "cat", "head", "tail", "wc", "grep",
        "which", "where", "tree", "stat",

        // The same commands under their PowerShell names. ls, cat and pwd are
        // aliases of these three, so leaving them out would make the rule depend
        // on which name the model happened to type.
        "Get-ChildItem", "Get-Content", "Get-Location",
    };

    /// <summary>
    /// Commands that never get a prefix rule, for one of two reasons.
    ///
    /// A shell, interpreter, wrapper or package runner takes the command it runs
    /// from its own arguments, so a prefix rule on one of them is a rule for
    /// "run anything".
    ///
    /// A command that writes or deletes takes a path where another command takes
    /// a subcommand, and a prefix rule would reach past that one path: approving
    /// <c>rm old</c> would also allow <c>rm older-and-still-needed</c>.
    /// </summary>
    private static readonly HashSet<string> ExactRuleOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        // Shells
        "bash", "sh", "zsh", "dash", "ksh", "csh", "fish",
        "cmd", "command", "powershell", "pwsh", "wsl",

        // Interpreters
        "python", "python2", "python3", "py", "node", "nodejs", "deno", "bun",
        "perl", "ruby", "php", "osascript",

        // Wrappers, and anything that hands its tail to another program
        "env", "xargs", "sudo", "doas", "su", "timeout", "nohup", "time", "nice",
        "stdbuf", "watch", "start", "ssh", "eval", "exec", "source",

        // Package runners
        "npx", "pnpx", "bunx", "uvx", "pipx",

        // Write, delete, or run a command of their own over what they find
        "rm", "rmdir", "mv", "cp", "chmod", "chown", "touch", "mkdir",
        "find", "sed", "awk",
    };

    /// <summary>
    /// Words that open, join or close a block. They arrive as the head of a
    /// segment because a for or if line is split on the same separators as a
    /// compound command.
    /// </summary>
    private static readonly HashSet<string> ShellKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        // POSIX shell
        "if", "then", "elif", "else", "fi", "for", "while", "until", "do", "done",
        "case", "esac", "in", "select", "function", "coproc", "[", "[[", "!",

        // PowerShell
        "foreach", "elseif", "switch", "try", "catch", "finally", "param", "data",
        "begin", "process", "end", "filter", "return", "break", "continue", "throw",
    };

    /// <summary>
    /// The path in the form the CLI's file rules are documented to take. Those
    /// follow gitignore syntax, where a single leading slash is relative to the
    /// settings file and an absolute path needs two. On Windows the CLI compares
    /// against the POSIX form, so <c>C:\src\a.cs</c> becomes <c>//c/src/a.cs</c>.
    ///
    /// Returns null for anything that would not name the one file on the banner:
    /// a relative path is resolved against a directory we cannot see, a glob
    /// matches more files, and a trailing separator or a <c>..</c> segment makes
    /// the rule cover a directory — gitignore syntax then covers everything
    /// under it.
    /// </summary>
    private static string? FileRulePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (path!.IndexOfAny(new[] { '*', '?', '[', ']', '!', '#', '{', '}' }) >= 0) return null;

        var p = path.Replace('\\', '/');

        if (p.EndsWith("/", StringComparison.Ordinal)) return null;
        if (p.Split('/').Any(part => part == "..")) return null;

        if (p.Length >= 3 && char.IsLetter(p[0]) && p[1] == ':' && p[2] == '/')
            return "//" + char.ToLowerInvariant(p[0]) + p.Substring(2);

        if (p.StartsWith("/", StringComparison.Ordinal) && !p.StartsWith("//", StringComparison.Ordinal))
            return "/" + p;

        return null;
    }

    private static string? ReadString(JsonElement input, string property)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        if (!input.TryGetProperty(property, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }
}

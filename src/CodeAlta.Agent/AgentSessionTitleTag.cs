using System.Text;

namespace CodeAlta.Agent;

/// <summary>
/// The <c>&lt;session_title&gt;</c> tag a model titles its session with. The tag is removed from every text that
/// is shown; what the model wrote stays as it is in the conversation it is sent again.
/// </summary>
public static class AgentSessionTitleTag
{
    /// <summary>The opening tag.</summary>
    public const string Opening = "<session_title>";

    /// <summary>The closing tag.</summary>
    public const string Closing = "</session_title>";

    /// <summary>The longest content of a tag: a longer one is ordinary text.</summary>
    public const int MaxContentLength = 200;

    /// <summary>Removes the tags of a complete text.</summary>
    /// <param name="text">The text a model wrote.</param>
    /// <returns>The text without its tags, or <paramref name="text" /> itself when it has none.</returns>
    public static string? Strip(string? text) => Strip(text, out _);

    /// <summary>Removes the tags of a complete text and gives the title of the last one.</summary>
    /// <param name="text">The text a model wrote.</param>
    /// <param name="title">The content of the last tag that has one, on one line; <see langword="null" /> when there is none.</param>
    /// <returns>The text without its tags, or <paramref name="text" /> itself when it has none.</returns>
    public static string? Strip(string? text, out string? title)
    {
        title = null;
        if (text is null || !text.Contains(Opening, StringComparison.Ordinal)) return text;
        var filter = new Filter();
        var shown = filter.Append(text) + filter.Flush();
        title = filter.Title;
        return filter.Removed ? shown : text;
    }

    /// <summary>
    /// Removes the tags of a text that arrives in pieces, without ever letting a part of a tag through: it holds back
    /// what could be the start of a tag, and gives it back unchanged once it is known not to be one. A tag in a fenced
    /// code block, or right after a backtick, is quoted text and stays.
    /// </summary>
    public sealed class Filter
    {
        private readonly StringBuilder _held = new();
        private readonly StringBuilder _output = new();
        // Spaces and a line break that follow a tag alone on its line: they go with it.
        private readonly StringBuilder _afterTag = new();
        private bool _inFence;
        private bool _lineIndent = true;
        private char _fenceCharacter;
        private int _fenceRun;
        private char _last;
        private bool _shownText;
        private bool _heldAtLineStart;
        private State _state;

        private enum State
        {
            Text,
            Held,
            AfterTag,
            // After a tag that opened the text: the blank lines that follow are not shown either.
            Leading,
        }

        /// <summary>Gets the content of the last tag that had one, trimmed; <see langword="null" /> before any.</summary>
        public string? Title { get; private set; }

        /// <summary>Gets whether a tag was removed.</summary>
        public bool Removed { get; private set; }

        /// <summary>Takes the next piece of the text.</summary>
        /// <param name="text">The piece.</param>
        /// <returns>What can be shown now; empty while everything is held back.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="text" /> is <see langword="null" />.</exception>
        public string Append(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            foreach (var character in text) Take(character);
            return TakeOutput();
        }

        /// <summary>Ends the text: what is still held back is not a tag.</summary>
        /// <returns>The text that was held back.</returns>
        public string Flush()
        {
            // What is given back is read again, and may end with the start of another tag.
            while (_state is State.Held) Release();
            _afterTag.Clear();
            _state = State.Text;
            return TakeOutput();
        }

        private string TakeOutput()
        {
            if (_output.Length == 0) return string.Empty;
            var text = _output.ToString();
            _output.Clear();
            return text;
        }

        private void Take(char character)
        {
            switch (_state)
            {
                case State.Held:
                    TakeHeld(character);
                    return;
                case State.AfterTag:
                    if (character is ' ' or '\t' or '\r')
                    {
                        _afterTag.Append(character);
                        return;
                    }

                    if (character == '\n')
                    {
                        _afterTag.Clear();
                        _state = _shownText ? State.Text : State.Leading;
                        return;
                    }

                    // Something else is on the line of the tag: the line stays, without the tag.
                    _state = State.Text;
                    var spaces = _afterTag.ToString();
                    _afterTag.Clear();
                    foreach (var space in spaces) Take(space);
                    Take(character);
                    return;
                case State.Leading:
                    if (char.IsWhiteSpace(character)) return;
                    _state = State.Text;
                    Take(character);
                    return;
            }

            if (character == '<' && !_inFence && _last != '`')
            {
                _held.Append(character);
                _heldAtLineStart = _lineIndent;
                _state = State.Held;
                return;
            }

            Emit(character);
        }

        private void TakeHeld(char character)
        {
            _held.Append(character);
            var length = _held.Length;
            if (length <= Opening.Length)
            {
                if (character != Opening[length - 1]) Release();
                return;
            }

            if (character is '\n' or '\r' || length > Opening.Length + MaxContentLength + Closing.Length)
            {
                Release();
                return;
            }

            if (character != '>' || length < Opening.Length + Closing.Length) return;
            for (var index = 0; index < Closing.Length; index++)
            {
                if (_held[length - Closing.Length + index] != Closing[index]) return;
            }

            var content = _held.ToString(Opening.Length, length - Opening.Length - Closing.Length).Trim();
            _held.Clear();
            Removed = true;
            if (content.Length > 0) Title = content;
            _state = _heldAtLineStart ? State.AfterTag : State.Text;
        }

        // What was held is not a tag: its "<" is text, and the rest is read again (it may start a tag).
        private void Release()
        {
            var held = _held.ToString();
            _held.Clear();
            _state = State.Text;
            Emit(held[0]);
            for (var index = 1; index < held.Length; index++) Take(held[index]);
        }

        private void Emit(char character)
        {
            _output.Append(character);
            _last = character;
            if (character == '\n')
            {
                _lineIndent = true;
                _fenceRun = 0;
                return;
            }

            if (!char.IsWhiteSpace(character)) _shownText = true;
            if (!_lineIndent) return;
            if (character is '`' or '~' && (_fenceRun == 0 || character == _fenceCharacter))
            {
                _fenceCharacter = character;
                // A line that starts with three of them opens a code block, or closes the one that is open.
                if (++_fenceRun == 3)
                {
                    _inFence = !_inFence;
                    _lineIndent = false;
                }

                return;
            }

            if (character is not (' ' or '\t') || _fenceRun > 0) _lineIndent = false;
        }
    }
}

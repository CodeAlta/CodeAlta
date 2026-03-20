namespace CodeAlta.Models;

internal readonly record struct StatusSnapshot(string Message, bool Busy, StatusTone Tone);
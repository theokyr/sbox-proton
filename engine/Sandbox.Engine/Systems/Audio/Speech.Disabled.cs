using System;
using System.Collections.ObjectModel;

namespace Sandbox.Speech;

/// <summary>
/// A result from speech recognition.
/// </summary>
public struct SpeechRecognitionResult
{
	/// <summary>
	/// From 0-1 how confident are we that this is the correct result?
	/// </summary>
	public float Confidence { get; init; }

	/// <summary>
	/// The text result from speech recognition.
	/// </summary>
	public string Text { get; init; }

	/// <summary>
	/// Did we successfully find a match?
	/// </summary>
	public bool Success { get; init; }
}

public static class Recognition
{
	/// <summary>
	/// Called when we have a result from speech recognition.
	/// </summary>
	/// <param name="result"></param>
	public delegate void OnSpeechResult( SpeechRecognitionResult result );

	/// <summary>
	/// Whether or not we are currently listening for speech.
	/// </summary>
	public static bool IsListening { get; private set; }

	/// <summary>
	/// Whether or not speech recognition is supported and a language is available.
	/// </summary>
	public static bool IsSupported => false;

	/// <summary>
	/// Start listening for speech to recognize as text.
	/// </summary>
	public static void Start( OnSpeechResult callback, IEnumerable<string> choices = null )
	{
		throw new PlatformNotSupportedException( "System.Speech is not available in this build." );
	}

	/// <summary>
	/// Stop any active listening for speech.
	/// </summary>
	public static void Stop()
	{
		IsListening = false;
	}

	internal static void Reset()
	{
		IsListening = false;
	}
}

/// <summary>
/// A speech synthesis stream. Lets you write text into speech and output it to a <see cref="SoundHandle"/>.
/// </summary>
public sealed class Synthesizer : IDisposable
{
	public record struct InstalledVoice( string Name, string Gender, string Age );

	public ReadOnlyCollection<InstalledVoice> InstalledVoices { get; } = Array.Empty<InstalledVoice>().AsReadOnly();

	public string CurrentVoice => string.Empty;

	public void Dispose()
	{
	}

	public Synthesizer TrySetVoice( string voiceName )
	{
		return this;
	}

	public Synthesizer TrySetVoice( string gender = "Male", string age = null )
	{
		return this;
	}

	public Synthesizer WithText( string input )
	{
		return this;
	}

	public Synthesizer OnVisemeReached( Action<int, TimeSpan> action )
	{
		return this;
	}

	public Synthesizer WithRate( int rate )
	{
		return this;
	}

	public Synthesizer WithBreak()
	{
		return this;
	}

	public SoundHandle Play()
	{
		throw new PlatformNotSupportedException( "System.Speech is not available in this build." );
	}
}

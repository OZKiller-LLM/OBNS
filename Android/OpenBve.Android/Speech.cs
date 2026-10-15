//Simplified BSD License (BSD-2-Clause)
//
//Copyright (c) 2026, The OpenBVE Android port contributors
//
//Redistribution and use in source and binary forms, with or without
//modification, are permitted provided that the following conditions are met:
//
//1. Redistributions of source code must retain the above copyright notice, this
//   list of conditions and the following disclaimer.
//2. Redistributions in binary form must reproduce the above copyright notice,
//   this list of conditions and the following disclaimer in the documentation
//   and/or other materials provided with the distribution.
//
//THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
//ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
//WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
//DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
//ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
//(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
//LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
//ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
//(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
//SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
using System;
using Android.Content;
using Android.Speech.Tts;
using Android.Views.Accessibility;

namespace OpenBve.Android
{
	/// <summary>
	/// Upstream speaks its messages, menus and the pause title through a screen reader when one is
	/// running (Tolk: NVDA, JAWS and the like on Windows). Here, when a screen reader runs on the phone
	/// (TalkBack or another touch-exploration service), the same text is spoken with Android's
	/// text-to-speech, in the game's language where the phone has a voice for it.
	/// </summary>
	internal static class Speech
	{
		private static TextToSpeech tts;
		private static bool ready;

		/// <summary>Whether a screen reader is running on the phone (upstream's ScreenReaderAvailable).</summary>
		internal static bool ScreenReaderRunning(Context context)
		{
			AccessibilityManager manager = context.GetSystemService(Context.AccessibilityService) as AccessibilityManager;
			return manager != null && manager.IsEnabled && manager.IsTouchExplorationEnabled;
		}

		/// <summary>Starts text-to-speech for the game (with the game's language), if a screen reader is running.</summary>
		internal static void Start(Context context, string languageCode)
		{
			if (tts != null || !ScreenReaderRunning(context))
			{
				return;
			}

			tts = new TextToSpeech(context.ApplicationContext, new InitListener(status =>
			{
				if (status != OperationResult.Success)
				{
					return;
				}

				try
				{
					if (!string.IsNullOrEmpty(languageCode))
					{
						tts.SetLanguage(Java.Util.Locale.ForLanguageTag(languageCode));
					}
				}
				catch (Exception)
				{
					// the phone's default voice, then
				}

				ready = true;
			}));
		}

		/// <summary>Speaks a text, as Tolk.Output: interrupting what is being said, or after it.</summary>
		internal static bool Say(string text, bool interrupt)
		{
			if (!ready || tts == null || string.IsNullOrWhiteSpace(text))
			{
				return false;
			}

			return tts.Speak(text, interrupt ? QueueMode.Flush : QueueMode.Add, null, "openbve") == OperationResult.Success;
		}

		/// <summary>Stops speaking and lets text-to-speech go.</summary>
		internal static void Stop()
		{
			ready = false;
			tts?.Stop();
			tts?.Shutdown();
			tts = null;
		}

		private sealed class InitListener : Java.Lang.Object, TextToSpeech.IOnInitListener
		{
			private readonly Action<OperationResult> done;

			internal InitListener(Action<OperationResult> done)
			{
				this.done = done;
			}

			public void OnInit(OperationResult status)
			{
				done(status);
			}
		}
	}
}

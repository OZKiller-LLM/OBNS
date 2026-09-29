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

// ReSharper disable once CheckNamespace
namespace System.Windows.Forms
{
	/// <summary>Buttons shown by a message box.</summary>
	public enum MessageBoxButtons
	{
		OK = 0,
		OKCancel = 1,
		AbortRetryIgnore = 2,
		YesNoCancel = 3,
		YesNo = 4,
		RetryCancel = 5
	}

	/// <summary>Icon shown by a message box.</summary>
	public enum MessageBoxIcon
	{
		None = 0,
		Hand = 16,
		Error = 16,
		Stop = 16,
		Question = 32,
		Exclamation = 48,
		Warning = 48,
		Asterisk = 64,
		Information = 64
	}

	/// <summary>The result of a dialog.</summary>
	public enum DialogResult
	{
		None = 0,
		OK = 1,
		Cancel = 2,
		Abort = 3,
		Retry = 4,
		Ignore = 5,
		Yes = 6,
		No = 7
	}

	/// <summary>
	/// Stands in for the Windows Forms message box. Android has no modal dialog available
	/// from arbitrary background threads, so messages are routed to logcat and to an
	/// optional handler that the Android front end can install.
	/// </summary>
	public static class MessageBox
	{
		/// <summary>Set by the Android front end to surface messages in the UI. May be null.</summary>
		public static Action<string, string, MessageBoxIcon> Handler;

		/// <summary>Shows a message.</summary>
		public static DialogResult Show(string text)
		{
			return Show(text, string.Empty, MessageBoxButtons.OK, MessageBoxIcon.None);
		}

		/// <summary>Shows a message.</summary>
		public static DialogResult Show(string text, string caption)
		{
			return Show(text, caption, MessageBoxButtons.OK, MessageBoxIcon.None);
		}

		/// <summary>Shows a message.</summary>
		public static DialogResult Show(string text, string caption, MessageBoxButtons buttons)
		{
			return Show(text, caption, buttons, MessageBoxIcon.None);
		}

		/// <summary>Shows a message.</summary>
		public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
		{
			string message = string.IsNullOrEmpty(caption) ? text : caption + ": " + text;
			if (icon == MessageBoxIcon.Error || icon == MessageBoxIcon.Warning)
			{
				Android.Util.Log.Warn("OpenBVE", message);
			}
			else
			{
				Android.Util.Log.Info("OpenBVE", message);
			}

			Handler?.Invoke(text, caption, icon);

			// Nothing can be answered without a user present; assume the non-destructive default.
			switch (buttons)
			{
				case MessageBoxButtons.YesNo:
				case MessageBoxButtons.YesNoCancel:
					return DialogResult.No;
				case MessageBoxButtons.OKCancel:
				case MessageBoxButtons.RetryCancel:
					return DialogResult.Cancel;
				default:
					return DialogResult.OK;
			}
		}
	}

	/// <summary>Stands in for the Windows Forms application object.</summary>
	public static class Application
	{
		/// <summary>
		/// The directory holding the program's data. The Android front end sets this to the
		/// app's private files directory during startup.
		/// </summary>
		public static string StartupPath { get; set; } = AppContext.BaseDirectory;

		/// <summary>The path of the executable. Not meaningful on Android.</summary>
		public static string ExecutablePath => StartupPath;

		/// <summary>Processes pending UI messages. A no-op on Android.</summary>
		public static void DoEvents()
		{
		}
	}
}

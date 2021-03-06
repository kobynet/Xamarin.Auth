//
//  Copyright 2012-2013, Xamarin Inc.
//
//    Licensed under the Apache License, Version 2.0 (the "License");
//    you may not use this file except in compliance with the License.
//    You may obtain a copy of the License at
//
//        http://www.apache.org/licenses/LICENSE-2.0
//
//    Unless required by applicable law or agreed to in writing, software
//    distributed under the License is distributed on an "AS IS" BASIS,
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//    See the License for the specific language governing permissions and
//    limitations under the License.
//
using System;
using System.Threading.Tasks;
using MonoTouch.UIKit;
using MonoTouch.Foundation;
using Xamarin.Utilities.iOS;
using Xamarin.Controls;

namespace Xamarin.Auth
{
	/// <summary>
	/// The ViewController that the WebAuthenticator presents to the user.
	/// </summary>
	internal class WebAuthenticatorController : UIViewController
	{
		protected WebAuthenticator authenticator;

		UIWebView webView;
		UIActivityIndicatorView activity;
		UIView authenticatingView;
		ProgressLabel progress;
		UIBarButtonItem cancelBarButtonItem;
		bool webViewVisible = true;

		const double TransitionTime = 0.25;

		bool keepTryingAfterError = true;

		public WebAuthenticatorController (WebAuthenticator authenticator)
		{
			this.authenticator = authenticator;

			authenticator.Error += HandleError;
			authenticator.BrowsingCompleted += HandleBrowsingCompleted;

			//
			// Create the UI
			//
			Title = authenticator.Title;

			if (authenticator.AllowCancel)
			{
				cancelBarButtonItem = NavigationItem.LeftBarButtonItem = new UIBarButtonItem (
					UIBarButtonSystemItem.Cancel,
					delegate {
					Cancel ();
				});				
			}

			webView = new UIWebView (View.Bounds) {
				Delegate = new WebViewDelegate (this),
				AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight,
				Hidden = true
			};
			View.AddSubview (webView);

			activity = new UIActivityIndicatorView (UIActivityIndicatorViewStyle.Gray) {
				AutoresizingMask = UIViewAutoresizing.FlexibleMargins,
				Center = View.Center
			};

			View.AddSubview (activity);
			View.BackgroundColor = UIColor.White;

			//
			// Locate our initial URL
			//
			BeginLoadingInitialUrl ();
		}

		void Cancel ()
		{
			authenticator.OnCancelled ();
		}

		void BeginLoadingInitialUrl ()
		{
			activity.StartAnimating ();

			authenticator.GetInitialUrlAsync ().ContinueWith (t => {
				if (t.IsFaulted) {
					keepTryingAfterError = false;
					authenticator.OnError (t.Exception);
					activity.StopAnimating ();
				}
				else {
					// Delete cookies so we can work with multiple accounts
					if (this.authenticator.ClearCookiesBeforeLogin)
						WebAuthenticator.ClearCookies();
					
					//
					// Begin displaying the page
					//
					LoadInitialUrl (t.Result);
				}
			}, TaskScheduler.FromCurrentSynchronizationContext ());
		}
		
		void LoadInitialUrl (Uri url)
		{
			if (!webViewVisible) {
				progress.StopAnimating ();
				webViewVisible = true;
				UIView.Transition (
					fromView: authenticatingView,
					toView: webView,
					duration: TransitionTime,
					options: UIViewAnimationOptions.TransitionCrossDissolve,
					completion: null);
			}

			if (url != null) {
				var request = new NSUrlRequest (new NSUrl (url.AbsoluteUri));
				NSUrlCache.SharedCache.RemoveCachedResponse (request); // Always try
				webView.LoadRequest (request);
			}
		}

		void HandleBrowsingCompleted (object sender, EventArgs e)
		{
			if (!webViewVisible) return;

			if (authenticatingView == null) {
				authenticatingView = new UIView (View.Bounds) {
					AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight,
					BackgroundColor = UIColor.FromRGB (0x33, 0x33, 0x33),
				};
				progress = new ProgressLabel ("Authenticating...");
				var f = progress.Frame;
				var b = authenticatingView.Bounds;
				f.X = (b.Width - f.Width) / 2;
				f.Y = (b.Height - f.Height) / 2;
				progress.Frame = f;
				authenticatingView.Add (progress);
			}
			else {
				authenticatingView.Frame = View.Bounds;
			}

			webViewVisible = false;

			progress.StartAnimating ();

			UIView.Transition (
				fromView: webView,
				toView: authenticatingView,
				duration: TransitionTime,
				options: UIViewAnimationOptions.TransitionCrossDissolve,
				completion: null);
		}

		void HandleError (object sender, AuthenticatorErrorEventArgs e)
		{
			var after = keepTryingAfterError ?
				(Action)BeginLoadingInitialUrl :
				(Action)Cancel;

			this.ShowError ("Connection Failed", "Check your Internet connection and try again.", after);
		}

		void MoveActivityToNavigationBar ()
		{
			activity.RemoveFromSuperview ();
			activity.ActivityIndicatorViewStyle = UIActivityIndicatorViewStyle.White;
			NavigationItem.RightBarButtonItem = new UIBarButtonItem (activity);
		}

		protected class WebViewDelegate : UIWebViewDelegate
		{
			protected WebAuthenticatorController controller;
			Uri lastUrl;
			bool ignoreLoadInterruptedError;

			public WebViewDelegate (WebAuthenticatorController controller)
			{
				this.controller = controller;
			}

			public override bool ShouldStartLoad (UIWebView webView, NSUrlRequest request, UIWebViewNavigationType navigationType)
			{
				var nsUrl = request.Url;

				if (nsUrl != null && !controller.authenticator.HasCompleted) {
					Uri url;
					if (Uri.TryCreate (nsUrl.AbsoluteString, UriKind.Absolute, out url)) {
						var shouldStartLoad = controller.authenticator.OnPageLoading (url);
						ignoreLoadInterruptedError = !shouldStartLoad;
						return shouldStartLoad;
					}
				}

				return true;
			}

			public override void LoadStarted (UIWebView webView)
			{
				controller.activity.StartAnimating ();

				webView.UserInteractionEnabled = false;
			}

			public override void LoadFailed (UIWebView webView, NSError error)
			{
				if (error.Domain == "NSURLErrorDomain" && error.Code == -999)
					return;

				controller.activity.StopAnimating ();
				controller.MoveActivityToNavigationBar ();

				webView.UserInteractionEnabled = true;
				webView.Hidden = false;

				if (!ignoreLoadInterruptedError || error.Code != 102)
					controller.authenticator.OnError (error.LocalizedDescription);
			}

			public override void LoadingFinished (UIWebView webView)
			{
				controller.activity.StopAnimating ();
				controller.MoveActivityToNavigationBar ();

				webView.UserInteractionEnabled = true;
				webView.Hidden = false;

				var url = new Uri (webView.Request.Url.AbsoluteString);
				if (url != lastUrl && !controller.authenticator.HasCompleted) {
					lastUrl = url;
					controller.authenticator.OnPageLoaded (url);
				}
			}
		}
	}
}


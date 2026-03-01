using Foundation;
using UIKit;
using Microsoft.Maui.Authentication;

namespace Supernova;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

	public override bool OpenUrl(UIApplication app, NSUrl url, NSDictionary options)
	{
		return WebAuthenticator.Default.OpenUrl(app, url, options);
	}
}

using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.Authentication;

namespace Supernova;

public sealed record OidcConfig(
	string Authority,
	string ClientId,
	string Scope,
	string RedirectUri,
	string? Audience = null);

public sealed record OidcTokens(
	string? AccessToken,
	string? IdToken,
	string? RefreshToken,
	int? ExpiresIn);

public sealed class OidcAuthService
{
	private readonly HttpClient _httpClient = new();

	public async Task<OidcTokens> SignInAsync(OidcConfig config, CancellationToken cancellationToken = default)
	{
		ValidateConfig(config);

		var discovery = await GetDiscoveryDocumentAsync(config, cancellationToken);
		var codeVerifier = CreateCodeVerifier();
		var codeChallenge = CreateCodeChallenge(codeVerifier);
		var state = CreateSecureRandom(24);
		var nonce = CreateSecureRandom(24);

		var startUri = BuildAuthorizeUri(discovery.AuthorizationEndpoint, config, state, nonce, codeChallenge);
		var callbackUri = new Uri(config.RedirectUri);

		WebAuthenticatorResult result;
		try
		{
			result = await WebAuthenticator.Default.AuthenticateAsync(startUri, callbackUri);
		}
		catch (TaskCanceledException)
		{
			throw new OperationCanceledException("Authentication canceled by user.");
		}

		var error = result.Properties.TryGetValue("error", out var errorValue) ? errorValue : null;
		if (!string.IsNullOrWhiteSpace(error))
		{
			throw new InvalidOperationException($"OIDC error: {error}");
		}

		var returnedState = result.Properties.TryGetValue("state", out var resultState) ? resultState : null;
		if (!string.Equals(state, returnedState, StringComparison.Ordinal))
		{
			throw new InvalidOperationException("OIDC state mismatch. Authentication response rejected.");
		}

		if (!result.Properties.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
		{
			throw new InvalidOperationException("OIDC authorization code missing from callback.");
		}

		return await ExchangeCodeAsync(discovery.TokenEndpoint, config, code, codeVerifier, cancellationToken);
	}

	private static void ValidateConfig(OidcConfig config)
	{
		if (config.Authority.Contains("YOUR_OIDC_PROVIDER", StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("Update OIDC authority/client settings in MainPage.xaml.cs before signing in.");
		}

		if (!Uri.TryCreate(config.Authority, UriKind.Absolute, out _))
		{
			throw new InvalidOperationException("OIDC authority must be an absolute URI.");
		}

		if (!Uri.TryCreate(config.RedirectUri, UriKind.Absolute, out _))
		{
			throw new InvalidOperationException("OIDC redirect URI must be an absolute URI.");
		}
	}

	private async Task<OidcDiscoveryDocument> GetDiscoveryDocumentAsync(OidcConfig config, CancellationToken cancellationToken)
	{
		var authority = config.Authority.TrimEnd('/');
		var discoveryUrl = $"{authority}/.well-known/openid-configuration";
		using var response = await _httpClient.GetAsync(discoveryUrl, cancellationToken);
		response.EnsureSuccessStatusCode();

		await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
		using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
		var root = document.RootElement;

		var authEndpoint = root.GetProperty("authorization_endpoint").GetString();
		var tokenEndpoint = root.GetProperty("token_endpoint").GetString();

		if (string.IsNullOrWhiteSpace(authEndpoint) || string.IsNullOrWhiteSpace(tokenEndpoint))
		{
			throw new InvalidOperationException("OIDC discovery document is missing required endpoints.");
		}

		return new OidcDiscoveryDocument(authEndpoint, tokenEndpoint);
	}

	private static Uri BuildAuthorizeUri(
		string authorizationEndpoint,
		OidcConfig config,
		string state,
		string nonce,
		string codeChallenge)
	{
		var query = new Dictionary<string, string>
		{
			["response_type"] = "code",
			["client_id"] = config.ClientId,
			["redirect_uri"] = config.RedirectUri,
			["scope"] = config.Scope,
			["code_challenge"] = codeChallenge,
			["code_challenge_method"] = "S256",
			["state"] = state,
			["nonce"] = nonce
		};

		if (!string.IsNullOrWhiteSpace(config.Audience))
		{
			query["audience"] = config.Audience!;
		}

		var queryString = string.Join("&", query.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));
		return new Uri($"{authorizationEndpoint}?{queryString}");
	}

	private async Task<OidcTokens> ExchangeCodeAsync(
		string tokenEndpoint,
		OidcConfig config,
		string code,
		string codeVerifier,
		CancellationToken cancellationToken)
	{
		var body = new Dictionary<string, string>
		{
			["grant_type"] = "authorization_code",
			["code"] = code,
			["redirect_uri"] = config.RedirectUri,
			["client_id"] = config.ClientId,
			["code_verifier"] = codeVerifier
		};

		if (!string.IsNullOrWhiteSpace(config.Audience))
		{
			body["audience"] = config.Audience!;
		}

		using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
		{
			Content = new FormUrlEncodedContent(body)
		};

		using var response = await _httpClient.SendAsync(request, cancellationToken);
		var content = await response.Content.ReadAsStringAsync(cancellationToken);

		if (!response.IsSuccessStatusCode)
		{
			throw new InvalidOperationException($"OIDC token request failed ({(int)response.StatusCode}): {content}");
		}

		using var document = JsonDocument.Parse(content);
		var root = document.RootElement;

		string? accessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
		string? idToken = root.TryGetProperty("id_token", out var it) ? it.GetString() : null;
		string? refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
		int? expiresIn = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var exp) ? exp : null;

		return new OidcTokens(accessToken, idToken, refreshToken, expiresIn);
	}

	public async Task<OidcTokens> SignInWithAuth0DeviceCodeAsync(
		OidcConfig config,
		Action<string>? status,
		CancellationToken cancellationToken = default)
	{
		ValidateConfig(config);

		var authority = config.Authority.TrimEnd('/');
		var deviceCodeEndpoint = $"{authority}/oauth/device/code";
		var tokenEndpoint = $"{authority}/oauth/token";

		var startBody = new Dictionary<string, string>
		{
			["client_id"] = config.ClientId,
			["scope"] = config.Scope,
		};

		if (!string.IsNullOrWhiteSpace(config.Audience))
		{
			startBody["audience"] = config.Audience!;
		}

		using var startRequest = new HttpRequestMessage(HttpMethod.Post, deviceCodeEndpoint)
		{
			Content = new FormUrlEncodedContent(startBody)
		};

		using var startResponse = await _httpClient.SendAsync(startRequest, cancellationToken);
		var startContent = await startResponse.Content.ReadAsStringAsync(cancellationToken);
		if (!startResponse.IsSuccessStatusCode)
		{
			throw new InvalidOperationException($"Auth0 device code start failed ({(int)startResponse.StatusCode}): {startContent}");
		}

		using var startJson = JsonDocument.Parse(startContent);
		var root = startJson.RootElement;

		var deviceCode = root.GetProperty("device_code").GetString();
		var userCode = root.GetProperty("user_code").GetString();
		var verificationUri = root.TryGetProperty("verification_uri_complete", out var complete)
			? complete.GetString()
			: root.GetProperty("verification_uri").GetString();
		var expiresIn = root.TryGetProperty("expires_in", out var expEl) && expEl.TryGetInt32(out var exp) ? exp : 600;
		var interval = root.TryGetProperty("interval", out var intEl) && intEl.TryGetInt32(out var intVal) ? intVal : 5;

		if (string.IsNullOrWhiteSpace(deviceCode) || string.IsNullOrWhiteSpace(verificationUri))
		{
			throw new InvalidOperationException("Auth0 device code response missing required fields.");
		}

		status?.Invoke($"Open browser and approve sign-in. Code: {userCode}");
		await Launcher.Default.OpenAsync(new Uri(verificationUri));

		var deadline = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
		while (DateTimeOffset.UtcNow < deadline)
		{
			cancellationToken.ThrowIfCancellationRequested();

			await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);

			var pollBody = new Dictionary<string, string>
			{
				["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
				["device_code"] = deviceCode!,
				["client_id"] = config.ClientId,
			};

			using var pollRequest = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
			{
				Content = new FormUrlEncodedContent(pollBody)
			};

			using var pollResponse = await _httpClient.SendAsync(pollRequest, cancellationToken);
			var pollContent = await pollResponse.Content.ReadAsStringAsync(cancellationToken);

			if (pollResponse.IsSuccessStatusCode)
			{
				using var tokenJson = JsonDocument.Parse(pollContent);
				var tokenRoot = tokenJson.RootElement;

				string? accessToken = tokenRoot.TryGetProperty("access_token", out var at) ? at.GetString() : null;
				string? idToken = tokenRoot.TryGetProperty("id_token", out var it) ? it.GetString() : null;
				string? refreshToken = tokenRoot.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
				int? tokenExpires = tokenRoot.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var exp2) ? exp2 : null;

				return new OidcTokens(accessToken, idToken, refreshToken, tokenExpires);
			}

			using var errorJson = JsonDocument.Parse(pollContent);
			var error = errorJson.RootElement.TryGetProperty("error", out var errorEl) ? errorEl.GetString() : "unknown_error";

			if (string.Equals(error, "authorization_pending", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			if (string.Equals(error, "slow_down", StringComparison.OrdinalIgnoreCase))
			{
				interval += 5;
				continue;
			}

			if (string.Equals(error, "access_denied", StringComparison.OrdinalIgnoreCase))
			{
				throw new OperationCanceledException("Authentication denied by user.");
			}

			if (string.Equals(error, "expired_token", StringComparison.OrdinalIgnoreCase))
			{
				throw new OperationCanceledException("Device code expired before sign-in completed.");
			}

			throw new InvalidOperationException($"Auth0 device flow failed: {pollContent}");
		}

		throw new OperationCanceledException("Device code expired before sign-in completed.");
	}

	private static string CreateCodeVerifier() => CreateSecureRandom(64);

	private static string CreateCodeChallenge(string codeVerifier)
	{
		var verifierBytes = Encoding.ASCII.GetBytes(codeVerifier);
		var hash = SHA256.HashData(verifierBytes);
		return Base64UrlEncode(hash);
	}

	private static string CreateSecureRandom(int numBytes)
	{
		Span<byte> bytes = stackalloc byte[numBytes];
		RandomNumberGenerator.Fill(bytes);
		return Base64UrlEncode(bytes.ToArray());
	}

	private static string Base64UrlEncode(byte[] bytes)
	{
		return Convert.ToBase64String(bytes)
			.TrimEnd('=')
			.Replace('+', '-')
			.Replace('/', '_');
	}

	private sealed record OidcDiscoveryDocument(string AuthorizationEndpoint, string TokenEndpoint);
}

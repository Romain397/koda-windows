using System.Net;
using System.Text.Json;

namespace Koda.Core.Tests;

/// <summary>A saved session has to come back: a restart and a profile switch both read it.</summary>
public class AccountSessionRestoreTests
{
    private static readonly YouTubeCookies Cookies = YouTubeCookies.FromHeader("SID=sid-value; SAPISID=sapisid-value");

    [Fact]
    public void SavedCookiesReadBackAsASignedInSession()
    {
        var cookies = new YouTubeCookies(
        [
            new YouTubeCookies.Cookie("SID", "sid-value"),
            new YouTubeCookies.Cookie("HSID", "hsid-value"),
            new YouTubeCookies.Cookie("SSID", "ssid-value"),
            new YouTubeCookies.Cookie("APISID", "apisid-value"),
            new YouTubeCookies.Cookie("SAPISID", "sapisid-value"),
        ]);

        var stored = JsonSerializer.Serialize(cookies, AccountJson.Options);
        var restored = JsonSerializer.Deserialize<YouTubeCookies>(stored, AccountJson.Options);

        Assert.NotNull(restored);
        Assert.True(restored!.LooksSignedIn);
        Assert.Equal(cookies, restored);
    }

    [Fact]
    public async Task ACompletedSignInSurvivesRestartAndSignOutClearsIt()
    {
        var folder = NewFolder();
        var secrets = new TestSecretStore();
        using (var music = Music(folder))
        using (var account = Account(music, folder, secrets))
        {
            account.Start();
            await account.CompleteSignInAsync(Cookies);
            Assert.True(account.IsSignedIn);
            Assert.NotNull(secrets.Read());

            var ticket = music.Accounts.Ticket()!;
            music.Accounts.Received(["SID=rotated; Domain=.youtube.com"], ticket);
        }

        using (var music = Music(folder))
        using (var account = Account(music, folder, secrets))
        {
            account.Start();
            Assert.True(account.IsSignedIn);
            Assert.Equal("Test listener", account.Info?.Name);
            Assert.Equal("rotated", music.Accounts.Active?.Cookies["SID"]);

            await account.SignOutAsync();
            Assert.Null(secrets.Read());
        }

        using var restartedMusic = Music(folder);
        using var restarted = Account(restartedMusic, folder, secrets);
        restarted.Start();
        Assert.False(restarted.IsSignedIn);
    }

    [Fact]
    public async Task AFailedCredentialWriteKeepsThePreviousSession()
    {
        var folder = NewFolder();
        var secrets = new TestSecretStore();
        using var music = Music(folder);
        using var account = Account(music, folder, secrets);
        account.Start();
        await account.CompleteSignInAsync(Cookies);
        var old = secrets.Read();

        secrets.FailWrites = true;
        var replacement = YouTubeCookies.FromHeader("SID=new-sid; SAPISID=new-sapisid");
        await Assert.ThrowsAsync<YouTubeSignInException>(() => account.CompleteSignInAsync(replacement));

        Assert.Equal(old, secrets.Read());
        Assert.Equal(Cookies, music.Accounts.Active?.Cookies);
    }

    [Fact]
    public async Task AResponseForThePreviousProfileDoesNotReplaceTheActiveProfilesSavedSession()
    {
        var firstFolder = NewFolder();
        var secondFolder = NewFolder();
        var first = new TestSecretStore();
        var second = new TestSecretStore();
        using var music = Music(firstFolder);
        using var account = Account(music, firstFolder, first);
        account.Start();
        await account.CompleteSignInAsync(Cookies);
        var oldTicket = music.Accounts.Ticket()!;

        await account.SwitchAsync(secondFolder, second, () => { });
        await account.CompleteSignInAsync(YouTubeCookies.FromHeader("SID=second; SAPISID=second-sapisid"));
        var saved = second.Read();
        music.Accounts.Received(["SID=late; Domain=.youtube.com"], oldTicket);

        Assert.Equal(saved, second.Read());
        await account.SwitchAsync(firstFolder, first, () => { });
        Assert.Equal(Cookies, music.Accounts.Active?.Cookies);
    }

    private static string NewFolder()
    {
        var folder = Path.Combine(Fixtures.Scratch, $"account-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static YouTubeMusic Music(string folder) => new(http: new HttpClient(new AccountMenuHandler()),
        visitorStorePath: Path.Combine(folder, "visitor.json"), signatureStorePath: Path.Combine(folder, "signature.json"));

    private static YouTubeAccount Account(YouTubeMusic music, string folder, ISecretStore secrets) =>
        new(music, new Library(folder), new Settings { SyncsYouTubeLibrary = false }, secrets, folder);

    private sealed class AccountMenuHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            const string menu = """
                {
                  "actions": [{ "openPopupAction": { "popup": { "multiPageMenuRenderer": {
                    "header": { "activeAccountHeaderRenderer": { "accountName": { "simpleText": "Test listener" } } }
                  } } } }],
                  "responseContext": { "logged_in": true, "visitorData": "TESTTOKEN%3D%3D" }
                }
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(menu) });
        }
    }
}

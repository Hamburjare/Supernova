using SpacetimeDB;

public static partial class Module
{
    private static readonly string[] AllowedIssuers =
    {
        // Example: "https://your-tenant.us.auth0.com/"
    };

    private static readonly string[] AllowedAudiences =
    {
        // Example: "your_auth0_client_id"
    };

    private static void EnsureAuthorized(ReducerContext ctx)
    {
        var jwt = ctx.SenderAuth.Jwt ?? throw new Exception("Unauthorized: JWT is required");

        if (AllowedIssuers.Length > 0 && !AllowedIssuers.Contains(jwt.Issuer, StringComparer.Ordinal))
        {
            throw new Exception($"Unauthorized: invalid issuer {jwt.Issuer}");
        }

        if (AllowedAudiences.Length > 0 && !AllowedAudiences.Any(aud => jwt.Audience.Contains(aud, StringComparer.Ordinal)))
        {
            throw new Exception("Unauthorized: invalid audience");
        }
    }

    [Table(Accessor = "User", Public = true)]
    public partial class User
    {
        [PrimaryKey]
        public Identity Identity;
        public string? Name;
        public bool Online;
    }

    [Table(Accessor = "Message", Public = true)]
    public partial class Message
    {
        public Identity Sender;
        public Timestamp Sent;
        public string Text = "";
    }

    [Reducer]
    public static void SetName(ReducerContext ctx, string name)
    {
        EnsureAuthorized(ctx);
        name = ValidateName(name);

        if (ctx.Db.User.Identity.Find(ctx.Sender) is User user)
        {
            user.Name = name;
            ctx.Db.User.Identity.Update(user);
        }
    }

    private static string ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new Exception("Names must not be empty");
        }
        return name;
    }

    [Reducer]
    public static void SendMessage(ReducerContext ctx, string text)
    {
        EnsureAuthorized(ctx);
        text = ValidateMessage(text);
        Log.Info(text);
        ctx.Db.Message.Insert(
            new Message
            {
                Sender = ctx.Sender,
                Text = text,
                Sent = ctx.Timestamp,
            }
        );
    }

    private static string ValidateMessage(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new ArgumentException("Messages must not be empty");
        }
        return text;
    }

    [Reducer(ReducerKind.ClientConnected)]
    public static void ClientConnected(ReducerContext ctx)
    {
        EnsureAuthorized(ctx);
        Log.Info($"Connect {ctx.Sender}");

        if (ctx.Db.User.Identity.Find(ctx.Sender) is User user)
        {
            user.Online = true;
            ctx.Db.User.Identity.Update(user);
        }
        else
        {
            ctx.Db.User.Insert(
                new User
                {
                    Name = null,
                    Identity = ctx.Sender,
                    Online = true,
                }
            );
        }
    }

    [Reducer(ReducerKind.ClientDisconnected)]
    public static void ClientDisconnected(ReducerContext ctx)
    {
        if (ctx.Db.User.Identity.Find(ctx.Sender) is User user)
        {
            user.Online = false;
            ctx.Db.User.Identity.Update(user);
        }
        else
        {
            Log.Warn("Warning: No user found for disconnected client.");
        }
    }
}

using System.Globalization;
using QoffeeAutomat.Core.Security;

namespace QoffeeAutomat.Hub.Security;

/// <summary>The outcome of creating an account.</summary>
public enum CreateUserOutcome
{
    /// <summary>The account was created.</summary>
    Created,

    /// <summary>An account with this name exists.</summary>
    UsernameTaken,
}

/// <summary>
/// The local accounts of the hub (PLAN.md S09 item 1, S20 "Hub security" item 1): the rules for names and passwords, the
/// verification of a password, and the changes to an account together with the end of its sessions. Passwords are stored as salted
/// PBKDF2-SHA256 hashes with at least 600 000 iterations (<see cref="PasswordHasher"/>).
/// </summary>
public sealed class UserService
{
    private const int MinUsernameLength = 2;
    private const int MaxUsernameLength = 64;

    // A hash of a random password: verifying against it takes as long as a real check, so the response time does not tell
    // whether a user name exists.
    private static readonly Lazy<string> DummyHash = new(
        () => PasswordHasher.Hash(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly UserStore _store;
    private readonly HubSessionStore _sessions;
    private readonly HubAuthOptions _options;
    private readonly TimeProvider _time;

    /// <summary>Creates the service.</summary>
    /// <param name="store">The table of accounts.</param>
    /// <param name="sessions">The sessions; they end when an account changes.</param>
    /// <param name="options">The password limits.</param>
    /// <param name="time">The clock.</param>
    public UserService(UserStore store, HubSessionStore sessions, HubAuthOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);

        _store = store;
        _sessions = sessions;
        _options = options;
        _time = time;
    }

    /// <summary>Checks a sign-in name: 2 to 64 characters, letters, digits and <c>. _ - @</c>, starting with a letter or digit.</summary>
    /// <param name="username">The candidate.</param>
    /// <returns>A message that says what is wrong, or null when the name is acceptable.</returns>
    public static string? ValidateUsername(string? username)
    {
        if (string.IsNullOrEmpty(username) || username.Length < MinUsernameLength || username.Length > MaxUsernameLength)
        {
            return "The user name must have 2 to 64 characters.";
        }

        if (!char.IsAsciiLetterOrDigit(username[0]))
        {
            return "The user name must start with a letter or a digit.";
        }

        foreach (char character in username)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or '@'))
            {
                return "The user name may only contain letters, digits and the characters . _ - @.";
            }
        }

        return null;
    }

    /// <summary>Checks a candidate password against the password rules of PLAN.md S09: at least 10 and at most 256 characters, and not the user name.</summary>
    /// <param name="password">The candidate.</param>
    /// <param name="username">The user name of the account, or null when it is not known yet.</param>
    /// <returns>A message that says what is wrong, or null when the password is acceptable.</returns>
    public string? ValidatePassword(string? password, string? username)
    {
        if (string.IsNullOrEmpty(password) || password.Length < _options.MinPasswordLength)
        {
            return "The password must have at least " + _options.MinPasswordLength.ToString(CultureInfo.InvariantCulture) + " characters.";
        }

        if (password.Length > _options.MaxPasswordLength)
        {
            return "The password must have at most " + _options.MaxPasswordLength.ToString(CultureInfo.InvariantCulture) + " characters.";
        }

        if (username is not null && string.Equals(password, username, StringComparison.OrdinalIgnoreCase))
        {
            return "The password must not be the user name.";
        }

        return null;
    }

    /// <summary>Creates an account. The caller has validated the name, the password and the role.</summary>
    /// <param name="username">The sign-in name.</param>
    /// <param name="password">The password.</param>
    /// <param name="role">The role.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The outcome and, when created, the account.</returns>
    public async Task<(CreateUserOutcome Outcome, HubUser? User)> CreateAsync(string username, string password, string role, CancellationToken cancellationToken)
    {
        HubUserRecord record = await Task.Run(() => NewRecord(username, password, role), cancellationToken).ConfigureAwait(false);
        bool created = await _store.TryCreateAsync(record, cancellationToken).ConfigureAwait(false);
        return created ? (CreateUserOutcome.Created, record.User) : (CreateUserOutcome.UsernameTaken, null);
    }

    /// <summary>Creates the first administrator, but only while the hub has no account (first-run setup).</summary>
    /// <param name="username">The sign-in name.</param>
    /// <param name="password">The password.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The administrator, or null when an account already exists.</returns>
    public async Task<HubUser?> CreateFirstAdministratorAsync(string username, string password, CancellationToken cancellationToken)
    {
        HubUserRecord record = await Task.Run(() => NewRecord(username, password, HubRoles.Admin), cancellationToken).ConfigureAwait(false);
        return await _store.TryCreateFirstAsync(record, cancellationToken).ConfigureAwait(false) ? record.User : null;
    }

    /// <summary>Verifies a user name and a password.</summary>
    /// <param name="username">The name.</param>
    /// <param name="password">The password.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The account, or null when the name is unknown, the account is disabled or the password is wrong; the caller cannot tell which.</returns>
    public async Task<HubUser?> AuthenticateAsync(string username, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(username);
        ArgumentNullException.ThrowIfNull(password);

        if (password.Length > _options.MaxPasswordLength || username.Length > MaxUsernameLength)
        {
            return null;
        }

        HubUserRecord? record = await _store.FindByUsernameAsync(username, cancellationToken).ConfigureAwait(false);
        string hash = record?.PasswordHash ?? DummyHash.Value;

        // 600 000 PBKDF2 iterations keep a core busy for a quarter of a second: not on the request thread.
        bool matches = await Task.Run(() => PasswordHasher.Verify(password, hash), cancellationToken).ConfigureAwait(false);
        if (record is null || !matches || record.User.Disabled)
        {
            return null;
        }

        DateTimeOffset now = _time.GetUtcNow();
        await _store.TouchLoginAsync(record.User.Id, now, cancellationToken).ConfigureAwait(false);
        return record.User with { LastLoginAt = now };
    }

    /// <summary>Checks the password of an account that is already known (change of the own password).</summary>
    /// <param name="userId">The id of the account.</param>
    /// <param name="password">The password to check.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when the account exists and the password matches.</returns>
    public async Task<bool> VerifyPasswordAsync(string userId, string password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentNullException.ThrowIfNull(password);

        if (password.Length > _options.MaxPasswordLength)
        {
            return false;
        }

        HubUserRecord? record = await _store.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false);
        string hash = record?.PasswordHash ?? DummyHash.Value;
        bool matches = await Task.Run(() => PasswordHasher.Verify(password, hash), cancellationToken).ConfigureAwait(false);
        return record is not null && matches;
    }

    /// <summary>Sets a new password and ends the sessions of the account, except one.</summary>
    /// <param name="userId">The account.</param>
    /// <param name="newPassword">The new password; the caller has validated it.</param>
    /// <param name="keepSessionId">The session that stays (the one of the user who changed their own password), or null.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when the account exists.</returns>
    public async Task<bool> SetPasswordAsync(string userId, string newPassword, string? keepSessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentNullException.ThrowIfNull(newPassword);

        string hash = await Task.Run(() => PasswordHasher.Hash(newPassword), cancellationToken).ConfigureAwait(false);
        bool found = await _store.SetPasswordHashAsync(userId, hash, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (found)
        {
            _sessions.RevokeUser(userId, keepSessionId);
        }

        return found;
    }

    /// <summary>Changes the role and/or the disabled flag; the sessions of the account end, so the change takes effect at once.</summary>
    /// <param name="userId">The account.</param>
    /// <param name="role">The new role, or null to keep it.</param>
    /// <param name="disabled">The new flag, or null to keep it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The outcome.</returns>
    public async Task<UserChangeResult> UpdateAsync(string userId, string? role, bool? disabled, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userId);

        UserChangeResult result = await _store.UpdateAsync(userId, role, disabled, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (result == UserChangeResult.Done)
        {
            _sessions.RevokeUser(userId);
        }

        return result;
    }

    /// <summary>Deletes an account and ends its sessions.</summary>
    /// <param name="userId">The account.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The outcome.</returns>
    public async Task<UserChangeResult> DeleteAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userId);

        UserChangeResult result = await _store.DeleteAsync(userId, cancellationToken).ConfigureAwait(false);
        if (result == UserChangeResult.Done)
        {
            _sessions.RevokeUser(userId);
        }

        return result;
    }

    /// <summary>Lists the accounts.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The accounts, ordered by name.</returns>
    public async Task<IReadOnlyList<HubUser>> ListAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<HubUserRecord> records = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        return [.. records.Select(record => record.User)];
    }

    /// <summary>Finds an account.</summary>
    /// <param name="userId">The id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The account, or null.</returns>
    public async Task<HubUser?> FindAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userId);

        return (await _store.FindByIdAsync(userId, cancellationToken).ConfigureAwait(false))?.User;
    }

    /// <summary>Counts the accounts.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The number of accounts.</returns>
    public Task<int> CountAsync(CancellationToken cancellationToken) => _store.CountAsync(cancellationToken);

    private HubUserRecord NewRecord(string username, string password, string role)
    {
        DateTimeOffset now = _time.GetUtcNow();
        var user = new HubUser(
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            username,
            role,
            Disabled: false,
            CreatedAt: now,
            UpdatedAt: now,
            PasswordChangedAt: now,
            LastLoginAt: null);
        return new HubUserRecord(user, PasswordHasher.Hash(password));
    }
}

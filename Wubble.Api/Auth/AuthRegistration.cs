using Wubble.Api.Users;

namespace Wubble.Api.Auth;

public sealed record AuthRegistration(
    UserResponse User,
    IssuedTokens Tokens);
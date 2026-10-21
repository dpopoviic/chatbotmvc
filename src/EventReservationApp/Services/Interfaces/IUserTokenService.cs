namespace EventReservationApp.Services.Interfaces
{
    // Short-lived signed tokens that carry the signed-in user's id from this app,
    // through Rasa, back to the internal agent API. Tokens are signed with a
    // private key only this app holds; Rasa verifies them with the public key,
    // so it can check a token but never forge one for another user.
    public interface IUserTokenService
    {
        // The roles go into the token's "roles" claim (always a JSON array) so
        // Rasa can limit knowledge base search to what the user may read.
        // correlationId goes into the "cid" claim and every token gets a new
        // "jti" (message id), so everything done for one chat message - in Rasa,
        // its actions, the MCP tools and the internal API - can be tied together.
        string CreateToken(string userId, IEnumerable<string> roles, string correlationId);

        // Returns the claims of a valid token, or null if the token is invalid,
        // expired, not issued by this app, or lacks the message/correlation id.
        Task<UserTokenClaims?> ValidateTokenAsync(string token);
    }

    // What a valid user token says: who the call is for (UserId), which chat
    // message it belongs to (MessageId, the "jti" claim) and which end-to-end
    // processing (CorrelationId, the "cid" claim). Signed, so Rasa or the LLM
    // cannot change any of them.
    public sealed record UserTokenClaims(string UserId, string MessageId, string CorrelationId);
}

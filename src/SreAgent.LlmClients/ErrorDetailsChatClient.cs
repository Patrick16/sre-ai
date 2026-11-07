using System.ClientModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace SreAgent.LlmClients;

/// <summary>
/// The OpenAI SDK reports a failed request as "Status: 400 (Bad Request)" and nothing else. The server's body
/// (LM Studio: "context length exceeded", "failed to parse tool call"...) is what explains it - put it in the message,
/// so it reaches the run's report and audit log.
/// </summary>
public sealed class ErrorDetailsChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken);
        }
        catch (ClientResultException ex)
        {
            throw Wrap(ex);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var e = base.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            ChatResponseUpdate update;
            try
            {
                if (!await e.MoveNextAsync()) yield break;
                update = e.Current;
            }
            catch (ClientResultException ex)
            {
                throw Wrap(ex);
            }
            yield return update;
        }
    }

    private static LlmRequestException Wrap(ClientResultException ex)
    {
        var body = ex.GetRawResponse()?.Content?.ToString() ?? "";
        if (body.Length > 1000) body = body[..1000] + "…";
        return new LlmRequestException($"LLM endpoint returned HTTP {ex.Status}: {(body.Length > 0 ? body : ex.Message)}", ex);
    }
}

public sealed class LlmRequestException(string message, Exception inner) : Exception(message, inner);

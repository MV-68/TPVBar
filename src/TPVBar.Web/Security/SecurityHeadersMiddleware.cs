namespace TPVBar.Web.Security;

/// <summary>
/// Cabeceras de seguridad para el navegador. La política CSP está ajustada a Blazor Server
/// (WebSocket al propio origen y estilos en línea de la propia interfaz); no se admiten scripts de terceros.
/// <para>
/// <c>script-src</c> admite 'unsafe-inline' porque Blazor emite un <i>import map</i> en línea (comprobado: sin él los
/// módulos de la interfaz no cargan). Es una debilidad conocida frente a XSS; se compensa con la codificación
/// automática de Razor y se podrá endurecer con un nonce cuando el framework lo permita (DECISIONS.md, D-054).
/// </para>
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline'; " +
        "object-src 'none'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        "connect-src 'self' ws: wss:; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'";

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Se fijan al empezar la respuesta (no antes) para sustituir a lo que añadan otros componentes: por ejemplo el
        // sistema antifalsificación añade «X-Frame-Options: SAMEORIGIN», y dos valores contradictorios invalidan la cabecera.
        context.Response.OnStarting(static state =>
        {
            var headers = ((HttpContext)state).Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "same-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            headers["Content-Security-Policy"] = ContentSecurityPolicy;
            return Task.CompletedTask;
        }, context);
        return next(context);
    }
}

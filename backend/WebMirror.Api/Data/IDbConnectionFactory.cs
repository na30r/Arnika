namespace WebMirror.Api.Data;

public interface IDbConnectionFactory
{
    Task<Microsoft.Data.SqlClient.SqlConnection> CreateOpenConnectionAsync(CancellationToken cancellationToken);
}

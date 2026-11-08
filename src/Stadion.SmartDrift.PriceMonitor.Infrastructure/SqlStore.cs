using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Stadion.SmartDrift.PriceMonitor.Infrastructure;

internal static class SqlStore
{
    public static Func<DbConnection> Factory(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return () => new SqlConnection(connectionString);
    }

    public static DbCommand Command(DbConnection connection, string sql, DbTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return command;
    }

    public static void Add(DbCommand command, string name, DbType type, object? value, int size = 0)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value ?? DBNull.Value;
        if (size != 0) parameter.Size = size;
        if (type == DbType.Decimal)
        {
            parameter.Precision = 18;
            parameter.Scale = 2;
        }
        command.Parameters.Add(parameter);
    }

    public static string? Text(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static string? Bound(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length];
}

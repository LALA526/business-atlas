using Npgsql;

namespace AccessApplicability.Infrastructure;

internal static class NpgsqlTransactionExtensions
{
    public static NpgsqlCommand CreateCommand(this NpgsqlTransaction transaction)
    {
        var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        return command;
    }
}

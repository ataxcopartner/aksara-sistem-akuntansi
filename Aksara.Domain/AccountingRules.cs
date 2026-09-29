namespace Aksara.Domain;

public sealed record JournalLineRule(string AccountCode, decimal Debit, decimal Credit);

public static class AccountingRules
{
    public static void ValidateBalancedJournal(IEnumerable<JournalLineRule> lines)
    {
        var normalized = lines.ToArray();
        if (normalized.Length < 2)
            throw new InvalidOperationException("Jurnal harus mempunyai minimal dua baris.");

        foreach (var line in normalized)
        {
            if (string.IsNullOrWhiteSpace(line.AccountCode))
                throw new InvalidOperationException("Kode perkiraan wajib diisi pada setiap baris jurnal.");
            if (line.Debit < 0 || line.Credit < 0)
                throw new InvalidOperationException("Nilai debit dan kredit tidak boleh negatif.");
            if (line.Debit > 0 && line.Credit > 0)
                throw new InvalidOperationException("Satu baris jurnal hanya boleh berisi debit atau kredit.");
            if (line.Debit == 0 && line.Credit == 0)
                throw new InvalidOperationException("Setiap baris jurnal wajib mempunyai nilai debit atau kredit.");
        }

        var debitTotal = Math.Round(normalized.Sum(x => x.Debit), 2);
        var creditTotal = Math.Round(normalized.Sum(x => x.Credit), 2);
        if (debitTotal <= 0 || creditTotal <= 0)
            throw new InvalidOperationException("Total debit dan kredit harus lebih dari nol.");
        if (debitTotal != creditTotal)
            throw new InvalidOperationException($"Jurnal belum seimbang. Debit {debitTotal:N2}, kredit {creditTotal:N2}.");
    }
}

namespace Aksara.Domain;

public static class PartnerBalanceRules
{
    public const string Opening = "OPENING";
    public const string Correction = "CORRECTION";
    public const string Increase = "INCREASE";
    public const string Decrease = "DECREASE";
    public const string PurchaseSource = "PURCHASE";
    public const string SaleSource = "SALE";
    public const string BalanceDocumentSource = "BALANCE_DOCUMENT";

    public static void ValidateDocument(
        string kind,
        string direction,
        decimal amount,
        string? targetSourceType,
        Guid? targetSourceId)
    {
        if (kind is not (Opening or Correction))
            throw new InvalidOperationException("Jenis saldo partner tidak dikenal.");
        if (direction is not (Increase or Decrease))
            throw new InvalidOperationException("Arah koreksi tidak dikenal.");
        if (amount <= 0)
            throw new InvalidOperationException("Jumlah harus lebih dari nol.");
        if (kind == Opening && direction != Increase)
            throw new InvalidOperationException("Saldo awal hanya dapat menambah saldo.");

        var hasTarget = targetSourceId is not null && !string.IsNullOrWhiteSpace(targetSourceType);
        if (kind == Correction && direction == Decrease && !hasTarget)
            throw new InvalidOperationException("Koreksi pengurang harus memilih bukti utang/piutang yang dikoreksi.");
        if ((kind == Opening || direction == Increase) && hasTarget)
            throw new InvalidOperationException("Target bukti hanya digunakan untuk koreksi pengurang.");
    }

    public static decimal Remaining(decimal originalAmount, decimal settledAmount, decimal reducedAmount)
    {
        if (originalAmount < 0 || settledAmount < 0 || reducedAmount < 0)
            throw new InvalidOperationException("Komponen saldo tidak boleh negatif.");
        return Math.Round(originalAmount - settledAmount - reducedAmount, 2);
    }

    public static void ValidateSettlement(decimal amount, decimal remaining)
    {
        if (amount <= 0)
            throw new InvalidOperationException("Jumlah pelunasan harus lebih dari nol.");
        if (remaining < 0 || amount > remaining)
            throw new InvalidOperationException("Jumlah pelunasan melebihi saldo yang tersedia.");
    }

    public static void ValidateDecrease(decimal amount, decimal remaining)
    {
        if (amount <= 0)
            throw new InvalidOperationException("Jumlah koreksi harus lebih dari nol.");
        if (remaining < 0 || amount > remaining)
            throw new InvalidOperationException("Koreksi pengurang melebihi sisa saldo bukti yang dipilih.");
    }

    public static void ValidateVersion(long expectedVersion, long currentVersion)
    {
        if (expectedVersion != currentVersion)
            throw new InvalidOperationException("Data sudah berubah oleh proses lain.");
    }

    public static IReadOnlyList<JournalLineRule> BuildJournal(
        string partnerType,
        string direction,
        decimal amount,
        string controlAccount,
        string offsetAccount)
    {
        if (partnerType is not ("SUPPLIER" or "CUSTOMER"))
            throw new InvalidOperationException("Jenis partner tidak dikenal.");
        if (direction is not (Increase or Decrease))
            throw new InvalidOperationException("Arah saldo tidak dikenal.");

        var controlDebit = partnerType == "CUSTOMER" ? direction == Increase : direction == Decrease;
        var lines = controlDebit
            ? new[]
            {
                new JournalLineRule(controlAccount, amount, 0),
                new JournalLineRule(offsetAccount, 0, amount)
            }
            : new[]
            {
                new JournalLineRule(offsetAccount, amount, 0),
                new JournalLineRule(controlAccount, 0, amount)
            };
        AccountingRules.ValidateBalancedJournal(lines);
        return lines;
    }
}

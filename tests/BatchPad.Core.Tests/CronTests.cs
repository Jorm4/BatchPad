using BatchPad.Core.Scheduling;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class CronTests
{
    internal static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

    internal static DateTimeOffset Local(int year, int month, int day, int hour, int minute) =>
        TriggerMath.ToInstant(new DateTime(year, month, day, hour, minute, 0), Berlin);

    private static DateTimeOffset? Next(string expression, DateTimeOffset after) => Cron.Parse(expression).Next(after, Berlin);

    [TestMethod]
    public void WeekdaysAtTwoAfterFridayMorningGiveMonday() =>
        Assert.AreEqual(Local(2026, 9, 28, 2, 0), Next("0 2 * * 1-5", Local(2026, 9, 25, 3, 0)));

    [TestMethod]
    public void QuarterHoursInWorkingHoursStepThroughTheDay()
    {
        Assert.AreEqual(Local(2026, 9, 25, 9, 0), Next("*/15 9-17 * * *", Local(2026, 9, 25, 8, 50)));
        Assert.AreEqual(Local(2026, 9, 25, 9, 15), Next("*/15 9-17 * * *", Local(2026, 9, 25, 9, 0)));
        Assert.AreEqual(Local(2026, 9, 25, 17, 45), Next("*/15 9-17 * * *", Local(2026, 9, 25, 17, 31)));
        Assert.AreEqual(Local(2026, 9, 26, 9, 0), Next("*/15 9-17 * * *", Local(2026, 9, 25, 17, 45)));
    }

    [TestMethod]
    public void NamesListsAndSteppedRangesParse()
    {
        Assert.AreEqual(Local(2027, 1, 1, 12, 0), Next("0 12 * JAN,jul mon-fri", Local(2026, 9, 25, 0, 0)));
        Assert.AreEqual(Local(2026, 9, 25, 10, 5), Next("5 0-23/10 * * *", Local(2026, 9, 25, 0, 5)));
        Assert.AreEqual(Local(2026, 9, 27, 0, 0), Next("0 0 * * 7", Local(2026, 9, 25, 0, 0)));
    }

    [TestMethod]
    public void WithBothDayFieldsRestrictedEitherMatches() =>
        Assert.AreEqual(Local(2026, 10, 2, 0, 0), Next("0 0 13 * fri", Local(2026, 9, 26, 0, 0)));

    [TestMethod]
    public void InvalidExpressionsAreRejected()
    {
        foreach (var expression in new[] { "60 * * * *", "* * *", "5-1 * * * *", "*/0 * * * *", "0 0 * foo *", "0 0 32 * *" })
            Assert.IsFalse(Cron.TryParse(expression, out _, out _), expression);
    }

    [TestMethod]
    public void ATimeSkippedBySpringForwardFiresWhenTheClocksJump()
    {
        var jump = new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero);
        Assert.AreEqual(jump, Next("30 2 * * *", Local(2026, 3, 29, 0, 0)));
        Assert.AreEqual(Local(2026, 3, 30, 2, 30), Next("30 2 * * *", jump));
    }

    [TestMethod]
    public void ATimeRepeatedByFallBackFiresOnce()
    {
        var first = new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.FromHours(2));
        Assert.AreEqual(first, Next("30 2 * * *", Local(2026, 10, 25, 0, 0)));
        Assert.AreEqual(Local(2026, 10, 26, 2, 30), Next("30 2 * * *", first));
    }
}

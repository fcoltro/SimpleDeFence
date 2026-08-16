using System.Collections.Generic;
using SimpleDeFence.DatabaseClasses;
using Xunit;

namespace SimpleDeFence.Tests
{
    public class AppDatabaseTests
    {
        [Fact]
        public void GetExceptionsForApp_returns_single_allow_all_exception_for_unrecognized_executable()
        {
            var db = new AppDatabase(); // empty KnownApplications
            var subject = new ExecutableSubject(@"C:\Games\SomeGame\game.exe");

            var exceptions = db.GetExceptionsForApp(subject, out var app);

            Assert.Null(app);
            var exception = Assert.Single(exceptions);
            Assert.Equal(subject, exception.Subject);
            Assert.IsType<TcpUdpPolicy>(exception.Policy);
        }
    }
}

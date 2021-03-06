﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace UnitTestProject1
{
    public class TestOutputLogger : ILogger
    {

        public TestOutputLogger(ITestOutputHelper output, string name)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            Output = output;
            Name = name;
        }

        public string Name { get; }

        public ITestOutputHelper Output { get; }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (formatter == null) throw new ArgumentNullException(nameof(formatter));
            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message)) return;
            var sb = new StringBuilder();
            sb.Append(logLevel);
            sb.Append(": ");
            var leftMargin = sb.Length;
            sb.Append(Name);
            if (LoggingScope.Current != null)
            {
                sb.AppendLine();
                sb.Append(' ', leftMargin);
                foreach (var scope in LoggingScope.Trace())
                {
                    sb.Append(" -> ");
                    sb.Append(scope.State);
                }
            }
            sb.AppendLine();
            sb.Append(' ', leftMargin);
            sb.Append(message);
            if (exception != null)
            {
                sb.AppendLine();
                sb.Append(' ', leftMargin);
                sb.Append(exception);
            }
            Output.WriteLine(sb.ToString());
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public IDisposable BeginScope<TState>(TState state)
        {
            return LoggingScope.Push(state);
        }

        private class LoggingScope : IDisposable
        {

            private static readonly AsyncLocal<LoggingScope> currentScope = new AsyncLocal<LoggingScope>();

            private LoggingScope(object state, LoggingScope parent)
            {
                State = state;
                Parent = parent;
            }

            public object State { get; }

            public LoggingScope Parent { get; }

            public static IEnumerable<LoggingScope> Trace()
            {
                var scope = Current;
                if (scope == null) return Enumerable.Empty<LoggingScope>();
                var stack = new Stack<LoggingScope>();
                while (scope != null)
                {
                    stack.Push(scope);
                    scope = scope.Parent;
                }
                return stack;
            }

            public static LoggingScope Current => currentScope.Value;

            public static LoggingScope Push(object state)
            {
                var current = currentScope.Value;
                var next = new LoggingScope(state, current);
                currentScope.Value = next;
                return next;
            }

            public void Dispose()
            {
                if (currentScope.Value != this) throw new InvalidOperationException();
                currentScope.Value = Parent;
            }
        }
    }

    public class TestOutputLoggerProvider : ILoggerProvider
    {

        public TestOutputLoggerProvider(ITestOutputHelper output)
        {
            Output = output ?? throw new ArgumentNullException(nameof(output));
        }

        public ITestOutputHelper Output { get; }

        public void Dispose()
        {

        }

        public ILogger CreateLogger(string categoryName)
        {
            return new TestOutputLogger(Output, categoryName);
        }
    }
}

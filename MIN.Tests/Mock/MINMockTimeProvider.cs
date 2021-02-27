using System;
using MIN.Abstractions;

namespace MIN.Tests.Mock
{
    public class MINMockTimeProvider : IMINTimeProvider
    {
        private DateTime now = new DateTime(2021, 1, 1, 12, 0, 0, DateTimeKind.Utc);


        public void SetNow(DateTime value)
        {
            now = value;
        }


        public DateTime Now()
        {
            return now;
        }
    }
}

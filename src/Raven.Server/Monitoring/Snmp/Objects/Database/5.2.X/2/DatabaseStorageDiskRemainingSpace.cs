using System.IO;
using Lextm.SharpSnmpLib;
using Raven.Server.Documents;
using Raven.Server.Utils;
using Sparrow;

namespace Raven.Server.Monitoring.Snmp.Objects.Database
{
    public class DatabaseStorageDiskRemainingSpace : DatabaseScalarObjectBase<Gauge32>
    {
        private static readonly Gauge32 Empty = new Gauge32(-1);

        public DatabaseStorageDiskRemainingSpace(string databaseName, DatabasesLandlord landlord, int index)
            : base(databaseName, landlord, "5.2.{0}.2.6", index)
        {
        }

        protected override Gauge32 GetData(DocumentDatabase database)
        {
            if (database.Configuration.Core.RunInMemory)
                return Empty;

            var result = DiskSpaceChecker.GetFreeDiskSpace(database.Configuration.Core.DataDirectory.FullPath, DriveInfo.GetDrives());
            if (result == null)
                return Empty;

            return new Gauge32(result.TotalFreeSpace.GetValue(SizeUnit.Megabytes));
        }
    }
}

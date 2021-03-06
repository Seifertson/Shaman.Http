﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Shaman.Types;

namespace Shaman.Runtime
{
    public struct DataTransferProgress
    {

        public DataTransferProgress(FileSize transferredData, FileSize? total, FileSize dataPerSecond)
            : this(transferredData, total, dataPerSecond, null)
        {
        }


        public DataTransferProgress(long transferredData, long? total, long dataPerSecond)
            : this(new FileSize(transferredData), total != null ? (FileSize?)new FileSize(total.Value) : null, new FileSize(dataPerSecond), null)
        {
        }

        public DataTransferProgress(FileSize transferredData, FileSize? total, FileSize dataPerSecond, string description)
        {
            this.transferredData = transferredData;
            this.total = total;
            this.dataPerSecond = dataPerSecond;
            this.description = description;
        }

        public DataTransferProgress(string description)
        {
            this.transferredData = default(FileSize);
            this.total = default(FileSize?);
            this.dataPerSecond = default(FileSize);
            this.description = description;
        }

        private string description;
        private FileSize? total;
        private FileSize transferredData;
        private FileSize dataPerSecond;

        public FileSize? Total { get { return total; } }
        public FileSize TransferredData { get { return transferredData; } }
        public FileSize DataPerSecond { get { return dataPerSecond; } }

        public string Description { get { return description; } }
        public override string ToString()
        {
            if (total == null) return transferredData.Bytes == 0 ? "0%" : (TransferredData.ToString() + " of Unknown (" + dataPerSecond.ToString() + " / sec)");
            if (total.Value == transferredData) return "Completed.";
            return
                (int)(100 * (float)TransferredData.Bytes / (float)Total.Value.Bytes) +
                "% - " + TransferredData.ToString() + " of " + Total.Value.ToString() +
                " (" + dataPerSecond.ToString() + " / sec)";
        }

        public double? Progress
        {
            get
            {
                if (total == null) return null;
                return (double)transferredData.Bytes / total.Value.Bytes;
            }
        }


    }
}

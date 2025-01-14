﻿using System;
using System.Collections.Generic;
using System.Linq;
using Raven.Client.ServerWide.Operations;
using Sparrow.Json.Parsing;

namespace Raven.Server.Dashboard
{
    public class DatabasesInfo : AbstractDashboardNotification
    {
        public override DashboardNotificationType Type => DashboardNotificationType.DatabasesInfo;

        public List<DatabaseInfoItem> Items { get; set; }

        public DatabasesInfo()
        {
            Items = new List<DatabaseInfoItem>();
        }

        public override DynamicJsonValue ToJson()
        {
            var json = base.ToJson();
            json[nameof(Items)] = new DynamicJsonArray(Items.Select(x => x.ToJson()));
            return json;
        }

        public override DynamicJsonValue ToJsonWithFilter(CanAccessDatabase filter)
        {
            var items = new DynamicJsonArray();
            foreach (var databaseInfoItem in Items)
            {
                if (filter(databaseInfoItem.Database, requiresWrite: false))
                {
                    items.Add(databaseInfoItem.ToJson());
                }
            }

            if (items.Count == 0)
                return null;

            var json = base.ToJson();
            json[nameof(Items)] = items;
            return json;
        }
    }

    public class DatabaseInfoItem : IDynamicJson
    {
        public string Database { get; set; }

        public long DocumentsCount { get; set; }

        public long IndexesCount { get; set; }

        public long ErroredIndexesCount { get; set; }

        public long AlertsCount { get; set; }

        public long PerformanceHintsCount { get; set; }

        public int ReplicationFactor { get; set; }

        public bool Online { get; set; }

        public bool Disabled { get; set; }

        public bool Irrelevant { get; set; }

        public long IndexingErrorsCount { get; set; }

        public BackupInfo BackupInfo { get; set; }
        
        public long OngoingTasksCount { get; set; }

        public DynamicJsonValue ToJson()
        {
            return new DynamicJsonValue
            {
                [nameof(Database)] = Database,
                [nameof(DocumentsCount)] = DocumentsCount,
                [nameof(IndexesCount)] = IndexesCount,
                [nameof(ErroredIndexesCount)] = ErroredIndexesCount,
                [nameof(IndexingErrorsCount)] = IndexingErrorsCount,
                [nameof(AlertsCount)] = AlertsCount,
                [nameof(PerformanceHintsCount)] = PerformanceHintsCount,
                [nameof(ReplicationFactor)] = ReplicationFactor,
                [nameof(BackupInfo)] = BackupInfo?.ToJson(),
                [nameof(Online)] = Online,
                [nameof(Disabled)] = Disabled,
                [nameof(Irrelevant)] = Irrelevant,
                [nameof(OngoingTasksCount)] = OngoingTasksCount
            };
        }
    }
}

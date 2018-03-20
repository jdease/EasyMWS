﻿// <auto-generated />
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Storage.Internal;
using MountainWarehouse.EasyMWS;
using MountainWarehouse.EasyMWS.Data;
using MountainWarehouse.EasyMWS.Enums;
using System;

namespace MountainWarehouse.EasyMWS.Migrations
{
    [DbContext(typeof(EasyMwsContext))]
    [Migration("20180314143257_InitialMigration")]
    partial class InitialMigration
    {
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "2.0.1-rtm-125")
                .HasAnnotation("SqlServer:ValueGenerationStrategy", SqlServerValueGenerationStrategy.IdentityColumn);

            modelBuilder.Entity("MountainWarehouse.EasyMWS.Data.ReportRequestCallback", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd();

                    b.Property<int>("AmazonRegion");

                    b.Property<int>("ContentUpdateFrequency");

                    b.Property<string>("Data");

                    b.Property<string>("DataTypeName");

                    b.Property<string>("GeneratedReportId");

                    b.Property<DateTime?>("LastRequested");

                    b.Property<string>("MethodName");

                    b.Property<string>("ReportRequestData");

                    b.Property<string>("RequestReportId");

                    b.Property<int>("RequestRetryCount");

                    b.Property<string>("TypeName");

                    b.HasKey("Id");

                    b.HasIndex("RequestReportId", "GeneratedReportId");

                    b.ToTable("ReportRequestCallbacks");
                });
#pragma warning restore 612, 618
        }
    }
}

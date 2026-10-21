CREATE TABLE "Circuits" (
    "Id" bigint NOT NULL,
    "ChannelId" character varying(128) NOT NULL DEFAULT '',
    "OutboundDate" date,
    "OutboundSequence" integer NOT NULL,
    "InboundSequence" integer,
    "LastReceivedUtc" timestamp(0) without time zone,
    "Revision" bigint NOT NULL,
    CONSTRAINT "PK_Circuits" PRIMARY KEY ("Id")
);


CREATE TABLE "Flights" (
    "FPID" bigint NOT NULL,
    "Callsign" varchar NOT NULL DEFAULT '',
    "FlightRules" varchar NOT NULL,
    "FlightType" char(3) NOT NULL DEFAULT '',
    "SsrCode" char(5) NOT NULL DEFAULT '',
    "AircraftCode" char(6) NOT NULL DEFAULT '',
    "AircraftType" varchar NOT NULL DEFAULT '',
    "WTC" varchar NOT NULL,
    "AidEquipment" varchar NOT NULL DEFAULT '',
    "SurEquipment" varchar NOT NULL DEFAULT '',
    "RegMarking" char(20) NOT NULL DEFAULT '',
    "ADEP" char(4) NOT NULL DEFAULT '',
    "OrigADES" char(4) NOT NULL DEFAULT '',
    "ADES" char(4) NOT NULL DEFAULT '',
    "ALTN1" char(4) NOT NULL DEFAULT '',
    "ALTN2" char(4) NOT NULL DEFAULT '',
    "EOBT" timestamp(0) without time zone NOT NULL,
    "ETOT" timestamp(0) without time zone,
    "ELDT" timestamp(0) without time zone,
    "EIBT" timestamp(0) without time zone NOT NULL,
    "AOBT" timestamp(0) without time zone,
    "ATOT" timestamp(0) without time zone,
    "ALDT" timestamp(0) without time zone,
    "AIBT" timestamp(0) without time zone,
    "SOBT" timestamp(0) without time zone NOT NULL,
    "SIBT" timestamp(0) without time zone NOT NULL,
    "IOBT" timestamp(0) without time zone,
    "TotalEET" interval(0) NOT NULL,
    "FiledRoute" text NOT NULL DEFAULT '',
    "AirwayCodes" text NOT NULL DEFAULT '',
    "ApprovedRoute" text NOT NULL DEFAULT '',
    "ApprovedArcType" varchar NOT NULL DEFAULT '',
    "DeviateRoute" text DEFAULT '',
    "OtherInfo" text NOT NULL DEFAULT '',
    "IsFiled" boolean NOT NULL,
    "IsDeleted" boolean NOT NULL,
    "IsAbolished" boolean NOT NULL,
    "Jurisdiction" varchar NOT NULL DEFAULT '',
    "ControlAreas" varchar NOT NULL DEFAULT '',
    "Remarks" text NOT NULL DEFAULT '',
    "State" varchar NOT NULL,
    "CreateTime" timestamp(0) without time zone NOT NULL,
    "Source" varchar NOT NULL,
    "SourceId" varchar NOT NULL DEFAULT '',
    "AircraftNumber" integer NOT NULL,
    "IsVipFlight" boolean NOT NULL,
    "SourceInfo" varchar NOT NULL DEFAULT '',
    "GUFI" varchar DEFAULT '',
    "UpdateTime" timestamp(0) without time zone,
    "UpdateMode" varchar NOT NULL,
    "DistributeNodes" varchar NOT NULL DEFAULT '',
    "DesignateControlAreaCode" varchar NOT NULL DEFAULT '',
    "DataSyncSource" varchar DEFAULT '',
    "DataSyncFlags" integer NOT NULL,
    CONSTRAINT "PK_Flights" PRIMARY KEY ("FPID")
);


CREATE TABLE "MilitaryPlans" (
    "PlanID" bigint NOT NULL,
    "PlanType" varchar NOT NULL DEFAULT '',
    "PlanUser" varchar NOT NULL DEFAULT '',
    "PlanDate" date NOT NULL,
    "EOBT" timestamp(0) without time zone,
    "EIBT" timestamp(0) without time zone,
    "Callsign" varchar NOT NULL DEFAULT '',
    "GroupNo" varchar NOT NULL DEFAULT '',
    "ADEP" varchar NOT NULL DEFAULT '',
    "ADES" varchar NOT NULL DEFAULT '',
    "PassAddr" varchar NOT NULL DEFAULT '',
    "Acfype" varchar NOT NULL DEFAULT '',
    "FlyRoute" text NOT NULL DEFAULT '',
    "Remarks" text NOT NULL DEFAULT '',
    "AttachFilePath" varchar NOT NULL DEFAULT '',
    "CreateUser" varchar NOT NULL DEFAULT '',
    "CheckUser" varchar NOT NULL DEFAULT '',
    "CheckTime" timestamp(0) without time zone,
    "DifferentFlag" boolean NOT NULL,
    "NewFlag" boolean NOT NULL,
    "EndFlag" boolean NOT NULL,
    "WillDeleteFlag" boolean NOT NULL,
    "HaltFlag" boolean NOT NULL,
    "UnitCode" varchar NOT NULL DEFAULT '',
    "CreateTime" timestamp(0) without time zone,
    "UpdateTime" timestamp(0) without time zone,
    "Order" integer,
    "MixPlanId" bigint,
    "FlightNumber" varchar NOT NULL DEFAULT '',
    "State" varchar NOT NULL,
    "DesignateControlAreaCode" varchar NOT NULL DEFAULT '',
    "CreationTime" timestamp(0) without time zone NOT NULL,
    "LastModificationTime" timestamp(0) without time zone,
    "IsDeleted" boolean NOT NULL,
    "DeletionTime" timestamp(0) without time zone,
    "CreatorId" text DEFAULT '',
    "DeleterId" text DEFAULT '',
    "LastModifierId" text DEFAULT '',
    CONSTRAINT "PK_MilitaryPlans" PRIMARY KEY ("PlanID")
);


CREATE TABLE "Permission" (
    "PermissionID" bigint NOT NULL,
    "PermissionName" character varying(50) NOT NULL DEFAULT '',
    "PermissionType" varchar NOT NULL,
    "PermissionScope" varchar DEFAULT '',
    "PermissionKey" varchar DEFAULT '',
    "PermissionDesc" character varying(400) NOT NULL DEFAULT '',
    "ParentId" bigint,
    "Order" integer NOT NULL,
    "PermissionValue" text DEFAULT '',
    "CreationTime" timestamp(0) without time zone NOT NULL,
    "LastModificationTime" timestamp(0) without time zone,
    "IsDeleted" boolean NOT NULL,
    "DeletionTime" timestamp(0) without time zone,
    "CreatorId" text DEFAULT '',
    "DeleterId" text DEFAULT '',
    "LastModifierId" text DEFAULT '',
    "DataSyncSource" varchar DEFAULT '',
    "DataSyncFlags" integer NOT NULL,
    CONSTRAINT "PK_Permission" PRIMARY KEY ("PermissionID"),
    CONSTRAINT "FK_Permission_Permission_ParentId" FOREIGN KEY ("ParentId") REFERENCES "Permission" ("PermissionID")
);


CREATE TABLE "Role" (
    "RoleID" bigint NOT NULL,
    "RoleName" varchar NOT NULL DEFAULT '',
    "RoleDesc" varchar NOT NULL DEFAULT '',
    "CreationTime" timestamp(0) without time zone NOT NULL,
    "LastModificationTime" timestamp(0) without time zone,
    "IsDeleted" boolean NOT NULL,
    "DeletionTime" timestamp(0) without time zone,
    "CreatorId" text DEFAULT '',
    "DeleterId" text DEFAULT '',
    "LastModifierId" text DEFAULT '',
    "DataSyncSource" varchar DEFAULT '',
    "DataSyncFlags" integer NOT NULL,
    CONSTRAINT "PK_Role" PRIMARY KEY ("RoleID")
);


CREATE TABLE "Templates" (
    "TemplateId" bigint NOT NULL,
    "TemplateName" varchar NOT NULL DEFAULT '',
    "Version" integer NOT NULL,
    "Category" varchar NOT NULL,
    "FileNamePattern" varchar DEFAULT '',
    "Content" text NOT NULL,
    "CreationTime" timestamp(0) without time zone NOT NULL,
    "LastModificationTime" timestamp(0) without time zone,
    "IsDeleted" boolean NOT NULL,
    "DeletionTime" timestamp(0) without time zone,
    "CreatorId" text DEFAULT '',
    "DeleterId" text DEFAULT '',
    "LastModifierId" text DEFAULT '',
    CONSTRAINT "PK_Templates" PRIMARY KEY ("TemplateId")
);


CREATE TABLE "FlightArrInfo" (
    "Id" bigint NOT NULL,
    "TCHT" timestamp(0) without time zone,
    "SurfaceState" varchar NOT NULL DEFAULT '',
    "StateTime" timestamp(0) without time zone,
    "Taxiway" text NOT NULL DEFAULT '',
    "Stand" char(4) NOT NULL DEFAULT '',
    "Runway" char(4) NOT NULL DEFAULT '',
    "STAR" char(4) NOT NULL DEFAULT '',
    CONSTRAINT "PK_FlightArrInfo" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_FlightArrInfo_Flights_Id" FOREIGN KEY ("Id") REFERENCES "Flights" ("FPID") ON DELETE CASCADE
);


CREATE TABLE "FlightDepInfo" (
    "Id" bigint NOT NULL,
    "COBT" timestamp(0) without time zone,
    "CTOT" timestamp(0) without time zone,
    "TOBT" timestamp(0) without time zone,
    "TTOT" timestamp(0) without time zone,
    "ASAT" timestamp(0) without time zone,
    "SlotTime" timestamp(0) without time zone,
    "TAKT" timestamp(0) without time zone,
    "SurfaceState" varchar NOT NULL DEFAULT '',
    "StateTime" timestamp(0) without time zone,
    "Taxiway" text NOT NULL DEFAULT '',
    "Stand" char(4) NOT NULL DEFAULT '',
    "Runway" char(4) NOT NULL DEFAULT '',
    "SID" character varying(20) NOT NULL DEFAULT '',
    "IsFirstTrip" boolean NOT NULL,
    "CRIT" timestamp(0) without time zone,
    "AtisCode" character varying(1) NOT NULL DEFAULT '',
    "Frequency" character varying(10) NOT NULL DEFAULT '',
    "IniAltitude" integer NOT NULL,
    CONSTRAINT "PK_FlightDepInfo" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_FlightDepInfo_Flights_Id" FOREIGN KEY ("Id") REFERENCES "Flights" ("FPID") ON DELETE CASCADE
);


CREATE TABLE "FlightLabel" (
    "LabelDataId" bigint NOT NULL,
    "FPID" bigint NOT NULL,
    CONSTRAINT "PK_FlightLabel" PRIMARY KEY ("LabelDataId", "FPID"),
    CONSTRAINT "FK_FlightLabel_Flights_FPID" FOREIGN KEY ("FPID") REFERENCES "Flights" ("FPID") ON DELETE CASCADE
);


CREATE TABLE "FlightRteInfo" (
    "FRID" bigint NOT NULL,
    "FPID" bigint NOT NULL,
    "RteAirwayCode" varchar NOT NULL DEFAULT '',
    "RgnPoints" text NOT NULL DEFAULT '',
    "RteControlAreas" varchar NOT NULL DEFAULT '',
    "RtePoints" text NOT NULL DEFAULT '',
    CONSTRAINT "PK_FlightRteInfo" PRIMARY KEY ("FRID"),
    CONSTRAINT "FK_FlightRteInfo_Flights_FPID" FOREIGN KEY ("FPID") REFERENCES "Flights" ("FPID") ON DELETE CASCADE
);


CREATE TABLE "OrganUnit" (
    "UnitID" bigint NOT NULL,
    "ParentId" bigint,
    "OrganName" character varying(50) NOT NULL DEFAULT '',
    "OrganDesc" varchar NOT NULL DEFAULT '',
    "OrganAlias" character varying(400) NOT NULL DEFAULT '',
    "RoleId" bigint NOT NULL,
    "ControlAreas" character varying(200) NOT NULL DEFAULT '',
    "Code" text NOT NULL DEFAULT '',
    "RelatedAirports" character varying(200) NOT NULL DEFAULT '',
    "CreationTime" timestamp(0) without time zone NOT NULL,
    "LastModificationTime" timestamp(0) without time zone,
    "IsDeleted" boolean NOT NULL,
    "DeletionTime" timestamp(0) without time zone,
    "CreatorId" text DEFAULT '',
    "DeleterId" text DEFAULT '',
    "LastModifierId" text DEFAULT '',
    "DataSyncSource" varchar DEFAULT '',
    "DataSyncFlags" integer NOT NULL,
    CONSTRAINT "PK_OrganUnit" PRIMARY KEY ("UnitID"),
    CONSTRAINT "FK_OrganUnit_OrganUnit_ParentId" FOREIGN KEY ("ParentId") REFERENCES "OrganUnit" ("UnitID"),
    CONSTRAINT "FK_OrganUnit_Role_RoleId" FOREIGN KEY ("RoleId") REFERENCES "Role" ("RoleID") ON DELETE CASCADE
);


CREATE TABLE "PermissionRole" (
    "PermissionsId" bigint NOT NULL,
    "RoleId" bigint NOT NULL,
    CONSTRAINT "PK_PermissionRole" PRIMARY KEY ("PermissionsId", "RoleId"),
    CONSTRAINT "FK_PermissionRole_Permission_PermissionsId" FOREIGN KEY ("PermissionsId") REFERENCES "Permission" ("PermissionID") ON DELETE CASCADE,
    CONSTRAINT "FK_PermissionRole_Role_RoleId" FOREIGN KEY ("RoleId") REFERENCES "Role" ("RoleID") ON DELETE CASCADE
);


CREATE TABLE "RoleConfiguration" (
    "RCID" bigint NOT NULL,
    "RoleId" bigint NOT NULL,
    "Type" varchar NOT NULL DEFAULT '',
    "Value" text NOT NULL DEFAULT '',
    "DataSyncSource" varchar DEFAULT '',
    "DataSyncFlags" integer NOT NULL,
    CONSTRAINT "PK_RoleConfiguration" PRIMARY KEY ("RCID"),
    CONSTRAINT "FK_RoleConfiguration_Role_RoleId" FOREIGN KEY ("RoleId") REFERENCES "Role" ("RoleID") ON DELETE CASCADE
);


CREATE TABLE "Users" (
    "UserID" uuid NOT NULL,
    "Nickname" varchar NOT NULL DEFAULT '',
    "Username" varchar NOT NULL DEFAULT '',
    "Password" char(32) NOT NULL DEFAULT '',
    "UserDesc" varchar NOT NULL DEFAULT '',
    "IsUnitAdmin" boolean NOT NULL,
    "UserInfo" text,
    "OrganUnitId" bigint,
    "CreationTime" timestamp(0) without time zone NOT NULL,
    "LastModificationTime" timestamp(0) without time zone,
    "IsDeleted" boolean NOT NULL,
    "DeletionTime" timestamp(0) without time zone,
    "CreatorId" text DEFAULT '',
    "DeleterId" text DEFAULT '',
    "LastModifierId" text DEFAULT '',
    "DataSyncSource" varchar DEFAULT '',
    "DataSyncFlags" integer NOT NULL,
    CONSTRAINT "PK_Users" PRIMARY KEY ("UserID"),
    CONSTRAINT "FK_Users_OrganUnit_OrganUnitId" FOREIGN KEY ("OrganUnitId") REFERENCES "OrganUnit" ("UnitID")
);


CREATE TABLE "UserConfiguration" (
    "UCID" bigint NOT NULL,
    "UserId" uuid NOT NULL,
    "Type" varchar NOT NULL DEFAULT '',
    "Value" text NOT NULL DEFAULT '',
    "DataSyncSource" varchar DEFAULT '',
    "DataSyncFlags" integer NOT NULL,
    CONSTRAINT "PK_UserConfiguration" PRIMARY KEY ("UCID"),
    CONSTRAINT "FK_UserConfiguration_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("UserID") ON DELETE CASCADE
);


CREATE UNIQUE INDEX "IX_Circuits_ChannelId" ON "Circuits" ("ChannelId");


CREATE INDEX "IX_FlightLabel_FPID" ON "FlightLabel" ("FPID");


CREATE UNIQUE INDEX "IX_FlightRteInfo_FPID" ON "FlightRteInfo" ("FPID");


CREATE INDEX "IX_Flights_SOBT" ON "Flights" ("SOBT");


CREATE INDEX "IX_MilitaryPlans_MixPlanId" ON "MilitaryPlans" ("MixPlanId");


CREATE INDEX "IX_OrganUnit_ParentId" ON "OrganUnit" ("ParentId");


CREATE INDEX "IX_OrganUnit_RoleId" ON "OrganUnit" ("RoleId");


CREATE INDEX "IX_Permission_ParentId" ON "Permission" ("ParentId");


CREATE UNIQUE INDEX "IX_Permission_PermissionScope_PermissionKey" ON "Permission" ("PermissionScope", "PermissionKey") WHERE "IsDeleted" = FALSE AND NULLIF("PermissionScope", '') IS NOT NULL AND NULLIF("PermissionKey", '') IS NOT NULL;


CREATE INDEX "IX_PermissionRole_RoleId" ON "PermissionRole" ("RoleId");


CREATE INDEX "IX_RoleConfiguration_RoleId" ON "RoleConfiguration" ("RoleId");


CREATE INDEX "IX_UserConfiguration_UserId" ON "UserConfiguration" ("UserId");


CREATE INDEX "IX_Users_OrganUnitId" ON "Users" ("OrganUnitId");


CREATE UNIQUE INDEX "IX_Users_Username" ON "Users" ("Username") WHERE "IsDeleted" = FALSE;

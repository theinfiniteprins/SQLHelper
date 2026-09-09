
GO
/****** Object:  StoredProcedure [dbo].[usp_Request_SelectForApproval]    Script Date: 09-Sep-26 2:33:00 PM ******/
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

--	Created By A. Developer ON 09-04-2026
--	Modified By A. Developer ON 13-06-2026 | Changed FinalApprovalStatus To RequestStatus
--	Modified by B. Maintainer on 19-06-2026 | Added [OperationAllowTillDate] field in @dtOrgConfig and gave effect

--	[dbo].[usp_Request_SelectForApproval] 2026, 18479, NULL, '2026-03-16', '2026-04-01', 'Pending', NULL, NULL, NULL, 'RequestDate',0

ALTER PROCEDURE [dbo].[usp_Request_SelectForApproval]

		@PeriodYear					int,
		@PersonID					int,
		@RequestTypeID				int,
		@FromDate					datetime,
		@ToDate						datetime,
		@StatusType					nvarchar(20),
		@ApplicationType			nvarchar(50),
		@DepartmentID				int,
		@ApprovalAuthority			int,
		@DateFilterType				nvarchar(50),
		@IsPenalty				BIT,
		@IsShort				bit

AS
BEGIN
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED
SET NOCOUNT ON;

DECLARE	@StartTime	datetime
DECLARE	@EndTime	datetime
SET		@StartTime = [dbo].[GetSystemDate]();

BEGIN TRY

		DECLARE @CurrentDate									datetime		= CAST([dbo].[GetSystemDate]() AS DATE)
		DECLARE @CurrentMonth									int				= MONTH(@CurrentDate)
		DECLARE @CurrentEOMonth									date			= EOMONTH(@CurrentDate)
		DECLARE @CurrentYear									int				= YEAR(@CurrentDate)
		DECLARE @GetApprovalStatusRejected						nvarchar(50)	= [dbo].[GetApprovalStatusRejected]()
		DECLARE @GetApprovalStatusCancelled						nvarchar(50)	= [dbo].[GetApprovalStatusCancelled]();
		DECLARE @GetRequestType_Credit					nvarchar(50)	= [dbo].[GetRequestType_Credit]()
		DECLARE @GetApprovalStatusApproved						nvarchar(50)	= [dbo].[GetApprovalStatusApproved]()
		DECLARE @GetApprovalStatusPending						nvarchar(50)	= [dbo].[GetApprovalStatusPending]()
		DECLARE @GetApprovalStatusNotRequired					NVARCHAR(50)	= [dbo].[GetApprovalStatusNotRequired]()
		DECLARE @OrgType								nvarchar(100)
		DECLARE @CutOffFromDay								int
		DECLARE @CutOffToDay								int
		DECLARE @UserID											int
		DECLARE @FunctionalOrgRefID							INT
		DECLARE @GetDateFormat							NVARCHAR(50) = [dbo].[GetDateFormat]()
		DECLARE @GetDateFilterType_RequestDate2 					NVARCHAR(50) = [dbo].[GetDateFilterType_RequestDate]()
		DECLARE @GetDateFilterType_ApplicationDate				NVARCHAR(50) = [dbo].[GetDateFilterType_ApplicationDate]()
		DECLARE @GetOrgDeptType_Actual				NVARCHAR(50) = [dbo].[GetOrgDeptType_Actual]()
		DECLARE @GetOrgDeptType_Functional			NVARCHAR(50) = [dbo].[GetOrgDeptType_Functional]()
		DECLARE @GetOrgDeptType_Salary				NVARCHAR(50) = [dbo].[GetOrgDeptType_Salary]()


		DECLARE @PeriodStartDate date = DATEFROMPARTS(@PeriodYear, 1, 1);
		DECLARE @PeriodEndDate   date = DATEFROMPARTS(@PeriodYear + 1, 1, 1);

--========================================================================================================================================--
		DECLARE @dtOrgConfig		TABLE
		(
			[OrgID]										INT,
			[IsOperationAllowed]							BIT,
			[OrgType]								NVARCHAR(50),
			[IsOnlyPostCycleOperationAllowed]		BIT,
			[CutOffFromDate]								DATETIME,
			[IsFinalApprovalByOwner]				BIT,
			[Approval_IsShowRemainingBalance]				BIT,
			[Approval_IsMultipleApproval]		BIT,
			[IsAllowedAfterLocking]								BIT,
			[IsAllowToModifyDateAfter]			BIT,
			[OperationAllowTillDate]						DATETIME
		)
		INSERT INTO @dtOrgConfig
		SELECT
				[TVF_GetOrgConfig].[OrgID],
				[TVF_GetOrgConfig].[IsOperationAllowed],
				[TVF_GetOrgConfig].[OrgType],
				[TVF_GetOrgConfig].[IsOnlyPostCycleOperationAllowed],
				[TVF_GetOrgConfig].[CutOffFromDate],
				[TVF_GetOrgConfig].[IsFinalApprovalByOwner],
				[TVF_GetOrgConfig].[Approval_IsShowRemainingBalance],
				[TVF_GetOrgConfig].[Approval_IsMultipleApproval],
				[TVF_GetOrgConfig].[IsAllowedAfterLocking],
				[TVF_GetOrgConfig].[IsAllowToModifyDateAfter],
				DATEADD(MONTH, (-1) *[PastMonthLimitAfterLocking], [CutOffFromDate])

		FROM	[dbo].[TVF_GetOrgConfig](NULL,@CurrentDate,NULL) AS [TVF_GetOrgConfig]
--=================================================================================================================
		SELECT
				@UserID						= [dbo].[ORG_User].[UserID],
				@FunctionalOrgRefID		= [dbo].[ORG_Person].[FunctionalOrgID]

		FROM	[dbo].[ORG_Person]

		INNER JOIN @dtOrgConfig AS [dtOrgConfig]
		ON		[dbo].[ORG_Person].[FunctionalOrgID] = [dtOrgConfig].[OrgID]

		INNER JOIN [dbo].[ORG_User]
		ON		[dbo].[ORG_User].[PersonID] = [dbo].[ORG_Person].[PersonID]
		AND		[dbo].[ORG_User].[PersonID] = @PersonID
--=================================================================================================================
		DECLARE @TVF_GetUserWiseType TABLE
		(
				RequestTypeID		INT
		)
		INSERT INTO @TVF_GetUserWiseType
		SELECT
				[TVF_GetUserWiseType].[RequestTypeID]

		FROM	[dbo].[TVF_GetUserWiseType_Inline](@UserID, @FunctionalOrgRefID,@CurrentDate) AS [TVF_GetUserWiseType]
--========================================================================================================================================--
		DROP TABLE IF EXISTS #dtRequest
		CREATE TABLE #dtRequest
		(
			[RequestID]		INT,
			[PersonID]					INT,
			[RequestStatus]	NVARCHAR(50) COLLATE DATABASE_DEFAULT,
			[ApprovalLockLevel]			INT,
			[FromDate]					datetime,
			[ToDate]					datetime
		)
		INSERT INTO [#dtRequest]
		(
			[RequestID],
			[PersonID],
			[RequestStatus],
			[ApprovalLockLevel],
			[FromDate],
			[ToDate]
		)
		SELECT
				[dbo].[REQ_Request].[RequestID],
				[dbo].[REQ_Request].[PersonID],
				[dbo].[REQ_Request].[RequestStatus],
				[dbo].[REQ_Request].[ApprovalLockLevel],
				[dbo].[REQ_Request].[FromDate],
				[dbo].[REQ_Request].[ToDate]

		FROM [dbo].[REQ_RequestApproval]

		INNER JOIN [dbo].[REQ_Request]
		ON		[dbo].[REQ_Request].[RequestID] = [dbo].[REQ_RequestApproval].[RequestID]
		AND		[dbo].[REQ_RequestApproval].[ApprovalPersonID] = @PersonID
		AND		[dbo].[REQ_Request].[RequestStatus] <> @GetApprovalStatusCancelled
		AND		(
					@StatusType = 'All'
					OR
					(
						[dbo].[REQ_RequestApproval].[ApprovalStatus] = @StatusType
						AND
						(
							[dbo].[REQ_RequestApproval].[ApprovalStatus] <> @GetApprovalStatusPending
							OR
							(
								[dbo].[REQ_RequestApproval].[ApprovalLevel] = ([dbo].[REQ_Request].[ApprovalLockLevel] + 1) --Get only those which are pending from my level
								AND
								[dbo].[REQ_Request].[OwnerApprovalStatus] IS NULL --If HR Approved/Rejected than dont show here
								AND
								[dbo].[REQ_Request].[RequestStatus] = @GetApprovalStatusPending
							)
						)
					)
				)
		AND		[dbo].[REQ_Request].[CancellationDateTime] IS NULL
		AND		(@RequestTypeID IS NULL OR [dbo].[REQ_Request].[RequestTypeID] = @RequestTypeID)
		AND		(@ApplicationType IS NULL OR [dbo].[REQ_Request].[ApplicationType] = @ApplicationType)
		AND		ISNULL([dbo].[REQ_Request].[IsNotFilledPenalty],0) = 0
		AND		ISNULL([dbo].[REQ_Request].[IsApprovalDelayPenalty],0) = 0
		AND		(
					@IsPenalty IS NULL
					OR
					ISNULL([dbo].[REQ_Request].[IsPenalty],0) = @IsPenalty
				)
		AND		(
					@DateFilterType IS NULL
					OR
					(
						@FromDate IS NULL
						OR
						@ToDate IS NULL
					)
					OR
					(
						@DateFilterType = @GetDateFilterType_ApplicationDate
						AND
						(
							[dbo].[REQ_Request].[ApplicationDate] >= @FromDate
							AND
							[dbo].[REQ_Request].[ApplicationDate] <= @ToDate
						)
					)
					OR
					(
						@DateFilterType = @GetDateFilterType_RequestDate2
						AND
						(
							(
								[dbo].[REQ_Request].[FromDate] >= @FromDate
								AND
								[dbo].[REQ_Request].[FromDate] <= @ToDate
							)
							OR
							(
								[dbo].[REQ_Request].[ToDate] >= @FromDate
								AND
								[dbo].[REQ_Request].[ToDate] <= @ToDate
							)
						)
					)
				)

		INNER JOIN [dbo].[REQ_RequestType]
		ON		[dbo].[REQ_RequestType].[RequestTypeID] = [dbo].[REQ_Request].[RequestTypeID]
		AND		(
					@IsShort IS NULL
					OR
					[dbo].[REQ_RequestType].[IsShort] = @IsShort
				)

		INNER JOIN [dbo].[ORG_AuthorityType]
		ON		[dbo].[ORG_AuthorityType].[ReportingAuthorityTypeID] = [dbo].[REQ_RequestApproval].[ReportingAuthorityTypeID]
		AND		(@ApprovalAuthority IS NULL OR [dbo].[ORG_AuthorityType].[ReportingAuthorityTypeID] = @ApprovalAuthority)

		INNER JOIN @TVF_GetUserWiseType AS [TVF_GetUserWiseType]
		ON		[TVF_GetUserWiseType].[RequestTypeID] = [dbo].[REQ_Request].[RequestTypeID]

		INNER JOIN [dbo].[ORG_Person]
		ON		[dbo].[ORG_Person].[PersonID] = [dbo].[REQ_Request].[PersonID]
		AND		(@DepartmentID IS NULL OR [dbo].[ORG_Person].[FunctionalDeptID] = @DepartmentID)

		GROUP BY [dbo].[REQ_Request].[RequestID],
				[dbo].[REQ_Request].[PersonID],
				[dbo].[REQ_Request].[RequestStatus],
				[dbo].[REQ_Request].[ApprovalLockLevel],
				[dbo].[REQ_Request].[FromDate],
				[dbo].[REQ_Request].[ToDate]

--========================================================================================================================================--
		DECLARE	 @dtPersonPeriod			[dbo].[dtPK]

		INSERT INTO @dtPersonPeriod
		SELECT
				[dtRequest].[RequestID]

		FROM	[#dtRequest] AS [dtRequest]

		INNER JOIN [dbo].[ORG_PersonPeriod]
		ON		[dbo].[ORG_PersonPeriod].[PersonID] = [dtRequest].[PersonID]
		AND		[dbo].[ORG_PersonPeriod].[FromDate] <= [dtRequest].[ToDate]
		AND		[dbo].[ORG_PersonPeriod].[ToDate] >= [dtRequest].[FromDate]

		GROUP BY [dtRequest].[RequestID]

--========================================================================================================================================--

		DROP TABLE IF EXISTS #dtRequestApprovalDetails
		CREATE TABLE #dtRequestApprovalDetails
		(
			RequestID		INT,
			ApprovalStaffName		NVARCHAR(300)  COLLATE DATABASE_DEFAULT,
			ApprovalRemarks			nvarchar(1000)  COLLATE DATABASE_DEFAULT
		)
		INSERT INTO #dtRequestApprovalDetails
		(
			[RequestID],
			[ApprovalStaffName],
			[ApprovalRemarks]
		)
		SELECT
				[dtRequest].[RequestID],
				STRING_AGG([dbo].[ORG_Person].[PersonFullName] , ', ') AS [ApprovalStaffName],
				ISNULL(STRING_AGG([dbo].[REQ_RequestApproval].[ApprovalRemarks] , ', '),'') AS [ApprovalRemarks]

		FROM [#dtRequest] AS [dtRequest]

		LEFT OUTER JOIN [dbo].[REQ_RequestApproval]
		ON		[dtRequest].[RequestID] = [dbo].[REQ_RequestApproval].[RequestID]
		AND		[dbo].[REQ_RequestApproval].[ApprovalLevel] = CASE
																			WHEN [dtRequest].[RequestStatus] = @GetApprovalStatusPending
																			THEN [dtRequest].[ApprovalLockLevel] + 1
																			ELSE [dtRequest].[ApprovalLockLevel]
																		END
		AND		[dbo].[REQ_RequestApproval].[ApprovalStatus] = [dtRequest].[RequestStatus]

		LEFT OUTER JOIN [dbo].[ORG_Person]
		ON		[dbo].[REQ_RequestApproval].[ApprovalPersonID] = [dbo].[ORG_Person].[PersonID]

		GROUP BY [dtRequest].[RequestID]

--========================================================================================================================================--

			SELECT	Distinct
					[dbo].[REQ_Request].[RequestID],
					[dbo].[ORG_Person].[PersonID],
					[dbo].[ORG_Person].[PersonCode],
					[dbo].[ORG_Person].[PersonFullName],
					[dbo].[ORG_Department].[DepartmentShortName],
					COALESCE([dbo].[REQ_RequestType].[ShortName],[dbo].[REQ_RequestType].[RequestTypeName]) AS [RequestTypeName],
					[dbo].[REQ_RequestType].[RequestTypeID],
					[dbo].[REQ_RequestType].[IsWeekOffSelectionRequired],
					[dbo].[REQ_Request].[ApplicationType],
					[dbo].[REQ_Request].[ApplicationDate],
					[dbo].[REQ_Request].[RemainingBalance],
					ISNULL([dtOrgConfigForRow].[Approval_IsShowRemainingBalance],0) AS [IsShowRemainingBalanceInRequestApproval],
					ISNULL([dtOrgConfigForRow].[Approval_IsMultipleApproval],1) AS [Approval_IsMultipleApproval],
					CASE WHEN [dbo].[REQ_RequestType].[DeductionRate] > 0
						 THEN [dtAllocation].[UsedUnits] / ISNULL([dbo].[REQ_RequestType].[DeductionRate],1)
						 ELSE ISNULL([dtAllocation].[UsedUnits],0)
					END  AS  [UsedUnits],

					CASE WHEN [dbo].[REQ_RequestType].[DeductionRate] > 0
						 THEN [dtAllocation].[ClosingBalance] / ISNULL([dbo].[REQ_RequestType].[DeductionRate],1)
						 ELSE ISNULL([dtAllocation].[ClosingBalance],0)
					END AS  [ClosingBalance],

					[dbo].[REQ_Request].[FromTime],
					[dbo].[REQ_Request].[ToTime],
					[dbo].[REQ_Request].[QRScannedOutTime],
					[dbo].[REQ_Request].[QRScannedInTime],
					[dbo].[REQ_Request].[SelectedWeekOffDate],
					[dbo].[REQ_Request].[FromDate],
					[dbo].[REQ_Request].[FromDayType],
					[dbo].[REQ_Request].[ToDayType],
					[dbo].[REQ_Request].[ToDate],
					[ResponsiblePersonStaff].[PersonID] AS [ResponsiblePersonID],
					[ResponsiblePersonStaff].[PersonFullName] AS [ResponsiblePersonName],
					[dbo].[REQ_Request].[Duration],
					[dbo].[REQ_Request].[Reason],
					[dbo].[REQ_Request].[AttachmentPath],
					CASE WHEN [dbo].[REQ_Request].[AttachmentPath] IS NULL
						THEN	0
						ELSE	1
					END AS [IsAttachment],
					--[dbo].[ORG_AuthorityType].[ReportingAuthorityTypeName] AS [ApprovalLevel],
					[dbo].[REQ_RequestApproval].[ApprovalLevel] AS [ApprovalLevel],

					[dbo].[REQ_Request].[TotalDays],
					[dbo].[REQ_Request].[FinalApprovalStatus],
					[dbo].[REQ_Request].[RequestStatus],
					[dbo].[ORG_Person].[Mobile],
					CASE WHEN [dbo].[REQ_Request].[RequestStatus] = @GetApprovalStatusPending
						 THEN [dbo].[REQ_Request].[ApprovalLockLevel] + 1
						 ELSE [dbo].[REQ_Request].[ApprovalLockLevel]
					END AS [ApprovalLockLevel],

					CASE	WHEN	[dbo].[REQ_Request].[PersonPeriodCostID] IS NOT NULL
							THEN	0

							WHEN	[dtOrgConfigForRow].[IsFinalApprovalByOwner] = 1
							AND		[dbo].[REQ_Request].[OwnerApprovalStatus] <> @GetApprovalStatusPending
							THEN	0

							WHEN	([dtPersonPeriod].[PKID] IS NOT NULL)
							AND		[dbo].[REQ_Request].[ApplicationType] <> @GetRequestType_Credit
							AND		([dbo].[REQ_Request].[LateEntryDate] IS NULL OR [dbo].[REQ_Request].[LateCostYear] IS NOT NULL)
							AND		[dtOrgConfigForRow].[IsAllowedAfterLocking] = 0
							THEN	0

							WHEN	[dtOrgConfigForRow].[IsOperationAllowed] = 0
							THEN	0

							WHEN	[dtPersonPeriod].[PKID] IS NOT NULL
							AND		[dbo].[REQ_Request].[ApplicationType] <> @GetRequestType_Credit
							AND		(
										[dtOrgConfigForRow].[IsAllowedAfterLocking] = 0
										OR
										(
											[dtOrgConfigForRow].[IsAllowedAfterLocking] = 1
											AND
											[dbo].[REQ_Request].[ToDate] < [dtOrgConfigForRow].[OperationAllowTillDate]
										)
									)
							THEN	0

							WHEN	[dtOrgConfigForRow].[IsOnlyPostCycleOperationAllowed] = 1 AND [dbo].[REQ_Request].[ToDate] < [dtOrgConfigForRow].[CutOffFromDate]
							THEN	0

							WHEN	[dbo].[REQ_Request].[RequestStatus] = @GetApprovalStatusRejected AND [dbo].[REQ_Request].[LastApprovalStatusByUserID] <> @UserID
							THEN	0

							WHEN	[dbo].[REQ_RequestApproval].[ApprovalLevel] < [dbo].[REQ_Request].[ApprovalLockLevel]
							OR		[dbo].[REQ_RequestApproval].[ApprovalLevel] > ([dbo].[REQ_Request].[ApprovalLockLevel] + 1)
							THEN	0

							ELSE	1
					END AS IsAllowToChange,

					CASE	WHEN [dbo].[REQ_Request].[PersonPeriodCostID] IS NOT NULL
							THEN 'This request has already been considered in the generated salary. Therefore, it cannot be approved or rejected.'

							WHEN	(ISNULL([dtOrgConfigForRow].[IsFinalApprovalByOwner], 0) = 1  AND ([dbo].[REQ_Request].[OwnerApprovalStatus] <> @GetApprovalStatusPending))
							THEN	 [dbo].[REQ_Request].[OwnerApprovalStatus] + ' By Owner Hence You cannot Change.'

							WHEN	([dtPersonPeriod].[PKID] IS NOT NULL)
							AND		[dbo].[REQ_Request].[ApplicationType] <> @GetRequestType_Credit
							AND		([dbo].[REQ_Request].[LateEntryDate] IS NULL OR [dbo].[REQ_Request].[LateCostYear] IS NOT NULL)
							AND		[dtOrgConfigForRow].[IsAllowedAfterLocking] = 0
							THEN	'Attendance has already been locked for this period.so this request cannot be approved or rejected'

							WHEN	[dbo].[REQ_Request].[RequestStatus] = @GetApprovalStatusRejected AND [dbo].[REQ_Request].[LastApprovalStatusByUserID] <> @UserID
							THEN	CAST([dtRequestApprovalDetails].[ApprovalStaffName] AS NVARCHAR) COLLATE DATABASE_DEFAULT + ' has already ' + [dbo].[REQ_Request].[RequestStatus] COLLATE DATABASE_DEFAULT

							WHEN	[dtOrgConfigForRow].[IsOperationAllowed] = 0
							THEN	'Operation is not allowed.'

							WHEN	[dtPersonPeriod].[PKID] IS NOT NULL
							AND		[dbo].[REQ_Request].[ApplicationType] <> @GetRequestType_Credit
							AND		(
										[dtOrgConfigForRow].[IsAllowedAfterLocking] = 0
										OR
										(
											[dtOrgConfigForRow].[IsAllowedAfterLocking] = 1
											AND
											[dbo].[REQ_Request].[ToDate] < [dtOrgConfigForRow].[OperationAllowTillDate]
										)
									)
							THEN	'Attendance has already been locked for this period, so this request cannot be approved or rejected for '
									 + [dbo].[ORG_Person].[PersonFullName] +
									 '. Application Date: ' + FORMAT([dbo].[REQ_Request].[ApplicationDate], @GetDateFormat) + '.'

							WHEN	[dtOrgConfigForRow].[IsOnlyPostCycleOperationAllowed] = 1 AND [dbo].[REQ_Request].[ToDate] < [dtOrgConfigForRow].[CutOffFromDate]
							THEN	'Past Operation is not allowed.'

							WHEN	[dbo].[REQ_RequestApproval].[ApprovalLevel] < [dbo].[REQ_Request].[ApprovalLockLevel]
							THEN	'Higher Level Authority has already changed status. Hence you cannot change it.'

							WHEN	[dbo].[REQ_RequestApproval].[ApprovalLevel] > ([dbo].[REQ_Request].[ApprovalLockLevel] + 1)
							THEN	'Lower Level Authority Approval is pending. Hence you cannot change it.'

							ELSE ''
					END AS AllowToChangeMsg,

					CASE WHEN	[dbo].[REQ_Request].[OwnerApprovalStatus] IS NOT NULL
						 THEN	[dbo].[REQ_Request].[OwnerApprovalStatus] + ' By Owner'
						 WHEN	([dbo].[REQ_Request].[RequestStatus] = @GetApprovalStatusApproved OR [dbo].[REQ_Request].[RequestStatus] = @GetApprovalStatusRejected)
						 THEN	[dbo].[REQ_Request].[RequestStatus] + ' By ' + [dtRequestApprovalDetails].[ApprovalStaffName]
								+ '<br/> Approval Remarks : ' + [dtRequestApprovalDetails].[ApprovalRemarks]
						 ELSE	'To be Approve/Reject By ' + [dtRequestApprovalDetails].[ApprovalStaffName]
					END AS [ApprovalStatusToolTip],

					CASE WHEN	ISNULL([dtOrgConfigForRow].[IsAllowToModifyDateAfter], 0) = 1
						 AND	ISNULL([dtOrgConfigForRow].[IsFinalApprovalByOwner], 0)  = 0
						 AND	[dbo].[REQ_Request].[RequestStatus] = @GetApprovalStatusPending
						 AND	ISNULL([dbo].[REQ_Request].[IsPenalty],0) = 0
						 AND	ISNULL([dbo].[REQ_Request].[IsNotFilledPenalty],0)  = 0
						 AND	([dbo].[REQ_Request].[CancellationDateTime] IS NULL)
						 AND	([dtPersonPeriod].[PKID] IS NULL)
						 THEN	1
						 ELSE	0
						END AS		[IsAllowToEdit],
						[dbo].[REQ_Request].[RequestID] AS [RequestApplicationApprovalID],
						[dbo].[REQ_RequestApproval].[ApprovalStatus]


		FROM  #dtRequest AS [dtRequest]

		INNER JOIN [dbo].[REQ_Request]
		ON		[dbo].[REQ_Request].[RequestID] = [dtRequest].[RequestID]

		INNER JOIN	[dbo].[REQ_RequestApproval]
		ON		[dbo].[REQ_RequestApproval].[RequestID] = [dbo].[REQ_Request].[RequestID]
		AND		[dbo].[REQ_RequestApproval].[ApprovalPersonID] = @PersonID
		AND		(
					@StatusType = 'All'
					OR
					(
						[dbo].[REQ_RequestApproval].[ApprovalStatus] = @StatusType
						AND
						(
							[dbo].[REQ_RequestApproval].[ApprovalStatus] <> @GetApprovalStatusPending
							OR
							(
								[dbo].[REQ_RequestApproval].[ApprovalLevel] = ([dbo].[REQ_Request].[ApprovalLockLevel] + 1) --Get only those which are pending from my level
								AND
								[dbo].[REQ_Request].[OwnerApprovalStatus] IS NULL --If HR Approved/Rejected than dont show here
								AND
								[dbo].[REQ_Request].[RequestStatus] = @GetApprovalStatusPending
							)
						)
					)
				)

		INNER JOIN [dbo].[ORG_Person]
		ON		[dbo].[ORG_Person].[PersonID] = [dbo].[REQ_Request].[PersonID]

		INNER JOIN	[dbo].[ORG_Department]
		ON		[dbo].[ORG_Department].[DepartmentID] = [dbo].[ORG_Person].[FunctionalDeptID]

		INNER JOIN [dbo].[REQ_RequestType]
		ON		[dbo].[REQ_RequestType].[RequestTypeID] = [dbo].[REQ_Request].[RequestTypeID]

		INNER JOIN @dtOrgConfig AS [dtOrgConfigForRow]
		ON		[dtOrgConfigForRow].[OrgID] = [dbo].[ORG_Person].[FunctionalOrgID]

		INNER JOIN [#dtRequestApprovalDetails] AS [dtRequestApprovalDetails]
		ON		[dbo].[REQ_Request].[RequestID] = [dtRequestApprovalDetails].[RequestID]

		LEFT OUTER JOIN	[dbo].[REQ_RequestBalance] AS [dtAllocation]
		ON		[dtAllocation].[PersonID] = [dbo].[REQ_Request].[PersonID]
		AND		[dtAllocation].[RequestTypeID] = COALESCE([dbo].[REQ_RequestType].[DeductionFromTypeID],[dbo].[REQ_Request].[RequestTypeID])
		AND		[dbo].[REQ_Request].[FromDate] >= [dtAllocation].[FromDate]
		AND		(
					[dtAllocation].[ToDate] IS NULL
					OR
					[dbo].[REQ_Request].[FromDate] <= [dtAllocation].[ToDate]
				)

		LEFT OUTER JOIN [dbo].[ORG_AuthorityType]
		ON		[dbo].[ORG_AuthorityType].[ReportingAuthorityLevel] = CASE
																					WHEN [dbo].[REQ_Request].[RequestStatus] = @GetApprovalStatusPending
																					THEN [dbo].[REQ_Request].[ApprovalLockLevel] + 1
																					ELSE [dbo].[REQ_Request].[ApprovalLockLevel]
																				END

		LEFT OUTER JOIN @dtPersonPeriod AS dtPersonPeriod
		ON		dtPersonPeriod.PKID = [dbo].[REQ_Request].[RequestID]

		LEFT OUTER JOIN [dbo].[ORG_Person] AS [ResponsiblePersonStaff]
		ON		[dbo].[REQ_Request].[ResponsiblePersonRefID] = [ResponsiblePersonStaff].[PersonID]

		ORDER BY [dbo].[REQ_Request].[ApplicationDate] DESC,
				 [dbo].[REQ_Request].[FromDate] DESC,
				 [dbo].[REQ_Request].[ToDate] DESC



END TRY

BEGIN CATCH
DECLARE @SPDetail Varchar(MAX)
		SET @SPDetail = 'EXEC [dbo].[usp_Request_SelectForApproval]'
		 +' @PeriodYear ='  + ISNULL(CAST(@PeriodYear As NVarchar(20)),'NULL')
		 +', @PersonID ='  + ISNULL(CAST(@PersonID As NVarchar(20)),'NULL')
		 +', @RequestTypeID ='  + ISNULL(CAST(@RequestTypeID As NVarchar(20)),'NULL')
		 +', @FromDate ='  + ISNULL(CAST(@FromDate As NVarchar(20)),'NULL')
		 +', @ToDate		 ='  + ISNULL(CAST(@ToDate		 As NVarchar(20)),'NULL')
		 +', @StatusType ='  + ISNULL(CAST(@StatusType As NVarchar(20)),'NULL')
		 +', @DepartmentID	 ='  + ISNULL(CAST(@DepartmentID	 As NVarchar(20)),'NULL')
		 +', @ApprovalAuthority	 ='  + ISNULL(CAST(@ApprovalAuthority	 As NVarchar(20)),'NULL')
		 +', @DateFilterType	 ='  + ISNULL(CAST(@DateFilterType	 As NVarchar(20)),'NULL')
		 +', @IsPenalty	 ='  + ISNULL(CAST(@IsPenalty	 As NVarchar(20)),'NULL')
		 + ', @IsShort = ' + ISNULL(CAST(@IsShort AS nvarchar(20)),'NULL')

DECLARE @ErrorMessage	nvarchar(4000)
		DECLARE @ErrorProcedure	nvarchar(4000)
		DECLARE @ErrorSeverity	int
		DECLARE @ErrorState		int
		DECLARE @ErrorLine		int
		DECLARE @ErrorNumber	int

		SELECT	@ErrorNumber = ERROR_NUMBER(),@ErrorSeverity = ERROR_SEVERITY(),@ErrorState = ERROR_STATE(),@ErrorProcedure = ERROR_PROCEDURE(),@ErrorLine = ERROR_LINE(),@ErrorMessage = ERROR_MESSAGE()

		EXEC [dbo].[usp_LogError_Insert] @SPDetail, @ErrorMessage, @ErrorProcedure, @ErrorSeverity, @ErrorNumber, @ErrorLine, @ErrorState


;THROW
END CATCH

SET		@EndTime = [dbo].[GetSystemDate]()
EXEC	[dbo].[usp_LogExecution_Insert] '[dbo].[usp_Request_SelectForApproval]', @StartTime, @EndTime
END

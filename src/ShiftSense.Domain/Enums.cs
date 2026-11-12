namespace ShiftSense.Domain;

public enum Region
{
    IN,
    US,
}

public enum Role
{
    Employee,
    Manager,
}

public enum LeaveType
{
    /// <summary>India: earned leave. US: PTO. Both are the "vacation" bucket.</summary>
    Annual,
    Sick,
    /// <summary>India only: casual leave.</summary>
    Casual,
}

public enum RequestStatus
{
    Pending,
    Approved,
    Rejected,
    Cancelled,
}

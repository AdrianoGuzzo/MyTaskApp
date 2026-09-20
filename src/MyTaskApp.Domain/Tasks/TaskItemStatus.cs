namespace MyTaskApp.Domain.Tasks;

// Nome com prefixo para não colidir com System.Threading.Tasks.TaskStatus.
public enum TaskItemStatus
{
    Pending = 0,
    Completed = 1,
    Cancelled = 2,
}

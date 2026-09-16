using System.Reflection;
using BenchmarkDotNet.Running;

// 수신 처리기의 단일 스레드 정상 상태 처리량과 프레임당 할당을 잰다.
//
//   dotnet run -c Release --project benchmarks/SpaceLink.Benchmarks -- --filter *
//
// 시험 안에서 재던 처리량은 xUnit 이 시험 클래스를 병렬로 돌리는 동안 측정돼
// 기계 경합이 섞여 있었다. 측정은 이 프로젝트에서만 한다.
BenchmarkSwitcher.FromAssembly(Assembly.GetExecutingAssembly()).Run(args);

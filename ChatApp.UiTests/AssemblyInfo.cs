// [왜] 화면 자동 테스트는 진짜 마우스·키보드 입력과 창 포커스를 쓴다.
//      xUnit은 기본으로 테스트 클래스끼리 "동시에" 돌리는데, 그러면 두 테스트의 키 입력이 섞이고 포커스를 서로 빼앗아 실패한다.
//      이 프로젝트의 테스트는 하나씩 차례로 돌게 한다.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

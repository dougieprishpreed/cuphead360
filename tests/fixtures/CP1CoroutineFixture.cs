using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Synthetic engine-only fixture. This is not Cuphead gameplay evidence.
public sealed class CP1FixtureOwner : MonoBehaviour
{
}

public sealed class CP1FixtureParent
{
    private readonly CP1FixtureOwner parentOwner;
    private readonly CP1FixtureOwner childOwner;

    public int State;

    public CP1FixtureParent(CP1FixtureOwner parentOwner, CP1FixtureOwner childOwner)
    {
        this.parentOwner = parentOwner;
        this.childOwner = childOwner;
        List<int> metadataCompatibility = new List<int>();
        metadataCompatibility.Add(0);
        State = Identity<int>(0);
    }

    public IEnumerator Run()
    {
        State = 1;
        yield return childOwner.StartCoroutine(Delay());
        State = 2;
    }

    public IEnumerator RunRaw()
    {
        State = 1;
        yield return Delay();
        State = 2;
    }

    private IEnumerator Delay()
    {
        yield return new WaitForSeconds(0.5f);
    }

    private static T Identity<T>(T value)
    {
        return value;
    }
}

public static class CP1FixtureFactory
{
    public static CP1FixtureOwner Owner()
    {
        return new CP1FixtureOwner();
    }

    public static CP1FixtureParent Parent(CP1FixtureOwner parentOwner,
                                          CP1FixtureOwner childOwner)
    {
        return new CP1FixtureParent(parentOwner, childOwner);
    }

    public static IEnumerator ParentIterator(CP1FixtureParent parent)
    {
        return parent.Run();
    }

    public static IEnumerator RawIterator(CP1FixtureParent parent)
    {
        return parent.RunRaw();
    }

    public static void Stop(CP1FixtureOwner owner, Coroutine handle)
    {
        owner.StopCoroutine(handle);
    }

    public static void StopAll(CP1FixtureOwner owner)
    {
        owner.StopAllCoroutines();
    }
}

public sealed class CP1TimingFixture
{
    public int State;

    public IEnumerator YieldNull()
    {
        State = 1;
        yield return null;
        State = 2;
    }
}

public sealed class CP1RawOrderFixture
{
    private readonly CP1FixtureOwner owner;
    private readonly CP1OrderState orderState;
    public int ParentState;
    public int ChildState;
    public int ChildOrder;
    public int ParentOrder;

    public CP1RawOrderFixture(CP1FixtureOwner owner, CP1OrderState orderState)
    {
        this.owner = owner;
        this.orderState = orderState;
    }

    public IEnumerator Run()
    {
        ParentState = 2;
        yield return Child();
        ParentOrder = orderState.NextOrder();
        ParentState = 3;
    }

    private IEnumerator Child()
    {
        ChildState = 2;
        yield return null;
        ChildState = 3;
        ChildOrder = orderState.NextOrder();
    }
}

public sealed class CP1OrderState
{
    private int Sequence;

    public int NextOrder()
    {
        Sequence++;
        return Sequence;
    }
}

public sealed class CP1ReentrancyState
{
    public int NestedStarts;
}

public sealed class CP1ReentrantParent
{
    private readonly CP1FixtureOwner parentOwner;
    private readonly CP1FixtureOwner childOwner;
    private readonly CP1ReentrancyState state;
    public int ParentState;
    public int ChildState;
    public Coroutine ParentHandle;

    public CP1ReentrantParent(CP1FixtureOwner parentOwner, CP1FixtureOwner childOwner,
                              CP1ReentrancyState state)
    {
        this.parentOwner = parentOwner;
        this.childOwner = childOwner;
        this.state = state;
    }

    public IEnumerator JoinSelfStopFirst()
    {
        ParentState = 1;
        yield return childOwner.StartCoroutine(SelfStopFirst());
        ParentState = 2;
    }

    public IEnumerator JoinFaultedChild()
    {
        ParentState = 1;
        yield return childOwner.StartCoroutine(FaultAfterYield());
        ParentState = 2;
    }

    private IEnumerator FaultAfterYield()
    {
        yield return null;
        throw new System.InvalidOperationException("CP1 fixture coroutine fault");
    }

    public IEnumerator SelfStopFirst()
    {
        childOwner.StopAllCoroutines();
        childOwner.StartCoroutine(Nested());
        ChildState = 1;
        yield return new WaitForSeconds(1.0f);
        ChildState = 2;
    }

    public IEnumerator SelfStopLater()
    {
        ChildState = 1;
        yield return null;
        childOwner.StopAllCoroutines();
        childOwner.StartCoroutine(Nested());
        ChildState = 2;
        yield return new WaitForSeconds(1.0f);
        ChildState = 3;
    }

    public IEnumerator SelfStopComplete()
    {
        childOwner.StopAllCoroutines();
        ChildState = 1;
        yield break;
    }

    public IEnumerator RawParentStop()
    {
        ParentState = 1;
        yield return null;
        yield return RawChildThatStopsParent();
        ParentState = 2;
    }

    private IEnumerator RawChildThatStopsParent()
    {
        ChildState = 1;
        parentOwner.StopCoroutine(ParentHandle);
        parentOwner.StartCoroutine(Nested());
        yield return null;
        ChildState = 2;
    }

    private IEnumerator Nested()
    {
        state.NestedStarts++;
        yield return null;
    }
}

public sealed class CP1RecursiveIterator
{
    private readonly CP1FixtureOwner owner;
    private readonly int depth;
    private readonly int target;

    public CP1RecursiveIterator(CP1FixtureOwner owner, int depth, int target)
    {
        this.owner = owner;
        this.depth = depth;
        this.target = target;
    }

    public IEnumerator Run()
    {
        if (depth < target)
        {
            if (depth == 31)
            {
                owner.StartCoroutine(new CP1GuardVictimIterator(owner).Run());
            }
            else
            {
                owner.StartCoroutine(new CP1RecursiveIterator(
                    owner, depth + 1, target).Run());
            }
        }
        yield return null;
    }
}

public sealed class CP1GuardVictimIterator
{
    private readonly CP1FixtureOwner owner;

    public CP1GuardVictimIterator(CP1FixtureOwner owner)
    {
        this.owner = owner;
    }

    public IEnumerator Run()
    {
        if (owner != null)
        {
            throw new System.InvalidOperationException("depth-guard victim must not execute MoveNext");
        }
        yield return null;
    }
}

public static class CP1LedgerSites
{
    public static void FailA()
    {
        throw new System.InvalidOperationException("CP1 fixture fault A");
    }

    public static void FailZ()
    {
        throw new System.InvalidOperationException("CP1 fixture fault Z");
    }

    public static void DeepOuterOnly()
    {
        DeepChain(33);
    }

    public static void DeepRoot()
    {
        DeepOuterOnly();
    }

    private static void DeepChain(int depth)
    {
        if (depth > 0)
        {
            DeepChain(depth - 1);
            return;
        }
        FailA();
    }
}

public static class CP1ExtendedFixtureFactory
{
    public static IEnumerator NullTiming(CP1TimingFixture timing)
    {
        return timing.YieldNull();
    }

    public static CP1TimingFixture Timing()
    {
        return new CP1TimingFixture();
    }

    public static CP1OrderState OrderState()
    {
        return new CP1OrderState();
    }

    public static CP1RawOrderFixture RawOrder(CP1FixtureOwner owner, CP1OrderState orderState)
    {
        return new CP1RawOrderFixture(owner, orderState);
    }

    public static IEnumerator RawOrderIterator(CP1RawOrderFixture fixture)
    {
        return fixture.Run();
    }

    public static CP1ReentrancyState ReentrancyState()
    {
        return new CP1ReentrancyState();
    }

    public static int ReentrancyCount(CP1ReentrancyState state)
    {
        return state.NestedStarts;
    }

    public static CP1ReentrantParent ReentrantParent(CP1FixtureOwner parentOwner,
                                                      CP1FixtureOwner childOwner,
                                                      CP1ReentrancyState state)
    {
        return new CP1ReentrantParent(parentOwner, childOwner, state);
    }

    public static IEnumerator JoinSelfStopFirst(CP1ReentrantParent fixture)
    {
        return fixture.JoinSelfStopFirst();
    }

    public static IEnumerator JoinFaultedChild(CP1ReentrantParent fixture)
    {
        return fixture.JoinFaultedChild();
    }

    public static IEnumerator SelfStopLater(CP1ReentrantParent fixture)
    {
        return fixture.SelfStopLater();
    }

    public static IEnumerator SelfStopComplete(CP1ReentrantParent fixture)
    {
        return fixture.SelfStopComplete();
    }

    public static IEnumerator RawParentStop(CP1ReentrantParent fixture)
    {
        return fixture.RawParentStop();
    }

    public static IEnumerator Recursive(CP1FixtureOwner owner, int target)
    {
        return new CP1RecursiveIterator(owner, 0, target).Run();
    }

    public static int Probe()
    {
        return 42;
    }
}

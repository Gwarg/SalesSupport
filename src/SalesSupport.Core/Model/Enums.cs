namespace SalesSupport.Core.Model;

/// <summary>Provenance of a picture item. Rank on conflict: Rep > Call > Crm (docs/customer-picture.md).</summary>
public enum Source { Crm, Call, Rep }

public enum FactCategory { Situation, Need, Constraint, Budget, Timeline, Stakeholder, Pain, Preference, Other }

public enum Confidence { Low, Medium, High }

public enum ThreadKind { Discovery, Objection, CustomerQuestion }

public enum ThreadStatus { Open, Addressed, Parked }

public enum Salience { Low, Medium, High }

public enum Stance { Owns, Interested, Neutral, Rejected }

public enum ActionOwner { Rep, Customer }

public enum Speaker { Rep, Customer }

public enum SignalType { BuyingSignal, ObjectionRaised, QuestionFromCustomer, TopicShift, Correction, Smalltalk }

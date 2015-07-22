﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Pytocs.CodeModel
{
    public class CSharpStatementWriter : ICodeStatementVisitor<int>
    {
        private IndentingTextWriter writer;
        private CSharpExpressionWriter expWriter;
        private bool suppressSemi;

        public CSharpStatementWriter(IndentingTextWriter writer)
        {
            this.writer = writer;
            this.expWriter = new CSharpExpressionWriter(writer);
        }

        private void EndLineWithSemi()
        {
            if (!suppressSemi)
            {
                writer.Write(";");
                TerminateLine();
            }
        }

        public int VisitAssignment(CodeAssignStatement ass)
        {
            ass.Destination.Accept(expWriter);
            writer.Write(" = ");
            ass.Source.Accept(expWriter);
            EndLineWithSemi();
            return 0;
        }

        private void TerminateLine()
        {
            writer.WriteLine();
        }

        public int VisitBreak(CodeBreakStatement b)
        {
            writer.Write("break");
            EndLineWithSemi();
            return 0;
        }

        public int VisitComment(CodeCommentStatement c)
        {
            writer.Write("//");
            writer.Write(c.Comment);
            TerminateLine();
            return 0;
        }

        public int VisitContinue(CodeContinueStatement c)
        {
            writer.Write("continue");
            EndLineWithSemi();
            return 0;
        }

        public int VisitForeach(CodeForeachStatement f)
        {
            writer.Write("foreach");
            writer.Write(" (");
            writer.Write("var");
            writer.Write(" ");
            f.Variable.Accept(expWriter);
            writer.Write(" ");
            writer.Write("in");
            writer.Write(" ");
            f.Collection.Accept(expWriter);
            writer.Write(")");
            WriteStatements(f.Statements);
            writer.WriteLine();
            return 0;
        }

        public int VisitIf(CodeConditionStatement cond)
        {
            writer.Write("if");
            writer.Write(" (");
            cond.Condition.Accept(this.expWriter);
            writer.Write(")");
            WriteStatements(cond.TrueStatements);
            if (cond.FalseStatements.Count > 0)
            {
                writer.Write(" ");
                writer.Write("else");
                if (cond.FalseStatements.Count == 1)
                {
                    var elseIf = cond.FalseStatements[0] as CodeConditionStatement;
                    if (elseIf != null)
                    {
                        writer.Write(" ");
                        return VisitIf(elseIf);
                    }
                }
                WriteStatements(cond.FalseStatements);
            }
            writer.WriteLine();
            return 0;
        }


        public int VisitTry(CodeTryCatchFinallyStatement t)
        {
            writer.Write("try");
            WriteStatements(t.TryStatements);
            foreach (var clause in t.CatchClauses)
            {
                WriteCatchClause(clause);
            }
            if (t.FinallyStatements.Count > 0)
            {
                writer.Write(" ");
                writer.Write("finally");
                WriteStatements(t.FinallyStatements);
            }
            writer.WriteLine();
            return 0;
        }

        private void WriteCatchClause(CodeCatchClause clause)
        {
            writer.Write(" ");
            writer.Write("catch");
            if (clause.CatchExceptionType != null)
            {
                writer.Write(" (");
                expWriter.VisitTypeReference(clause.CatchExceptionType);
                if (!string.IsNullOrEmpty(clause.LocalName))
                {
                    writer.Write(" ");
                    writer.WriteName(clause.LocalName);
                }
                writer.Write(")");
            }
            WriteStatements(clause.Statements);
        }

        public int VisitPostTestLoop(CodePostTestLoopStatement loop)
        {
            writer.Write("do");
            WriteStatements(loop.Body);
            writer.Write(" ");
            writer.Write("while");
            writer.Write(" (");
            loop.Test.Accept(expWriter);
            writer.Write(");");
            TerminateLine();
            return 0;
        }

        public int VisitPreTestLoop(CodePreTestLoopStatement loop)
        {
            writer.Write("while");
            writer.Write(" (");
            loop.Test.Accept(expWriter);
            writer.Write(")");
            WriteStatements(loop.Body);
            writer.WriteLine();
            return 0;
        }

        public int VisitReturn(CodeMethodReturnStatement ret)
        {
            writer.Write("return");
            if (ret.Expression != null)
            {
                writer.Write(" ");
                ret.Expression.Accept(expWriter);
            }
            EndLineWithSemi();
            return 0;
        }

        public int VisitSideEffect(CodeExpressionStatement side)
        {
            side.Expression.Accept(expWriter);
            EndLineWithSemi();
            return 0;
        }

        public int VisitThrow(CodeThrowExceptionStatement t)
        {
            writer.Write("throw");
            if (t.Expression != null)
            {
                writer.Write(" ");
                t.Expression.Accept(expWriter);
            }
            EndLineWithSemi();
            return 0;
        }

        public void WriteStatements(List<CodeStatement> stm)
        {
            writer.Write(" {");
            TerminateLine();
            ++writer.IndentLevel;
            foreach (var s in stm)
            {
                s.Accept(this);
            }
            --writer.IndentLevel;
            writer.Write("}");
        }

        public int VisitUsing(CodeUsingStatement u)
        {
            writer.Write("using");
            writer.Write(" (");
            writer.Write("var");
            writer.Write(" ");
            var sep = "";
            bool old = suppressSemi;
            suppressSemi = true;
            foreach (var init in u.Initializers)
            {
                writer.Write(sep);
                sep = ", ";
                init.Accept(this);
            }
            suppressSemi = old;
            writer.Write(")");
            WriteStatements(u.Statements);
            writer.WriteLine();
            return 0;
        }

        public int VisitVariableDeclaration(CodeVariableDeclarationStatement decl)
        {
            expWriter.VisitTypeReference(decl.Type);
            writer.Write(" ");
            writer.WriteName(decl.Name);
            if (decl.InitExpression != null)
            {
                writer.Write(" = ");
                decl.InitExpression.Accept(expWriter);
            }
            writer.WriteLine(";");
            return 0;
        }

        public int VisitYield(CodeYieldStatement y)
        {
            writer.Write("yield");
            writer.Write(" "); 
            writer.Write("return");
            writer.Write(" ");
            y.Expression.Accept(expWriter);
            EndLineWithSemi();
            return 0;
        }
    }
}

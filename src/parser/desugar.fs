(*
  Copyright 2008-2014 Nikhil Swamy and Microsoft Research

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

  http://www.apache.org/licenses/LICENSE-2.0

  Unless required by applicable law or agreed to in writing, software
  distributed under the License is distributed on an "AS IS" BASIS,
  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
  See the License for the specific language governing permissions and
  limitations under the License.
*)
#light "off"
module Microsoft.FStar.Parser.Desugar

open Microsoft.FStar
open Microsoft.FStar.Parser
open Microsoft.FStar.Range
open Microsoft.FStar.Parser.AST
open Microsoft.FStar.Parser.DesugarEnv
open Microsoft.FStar.Absyn
open Microsoft.FStar.Absyn.Syntax
open Microsoft.FStar.Absyn.Util
open Microsoft.FStar.Util

let unparen t = match t.term with 
  | Paren t -> t
  | _ -> t

let kind_star r = mk_term (Name (lid_of_path ["Type"] r)) r Kind

let mk_app e args =
  List.fold_left
    (fun e -> function
      | Inl t, imp -> 
        if imp then raise (Error ("Type applications at the term level are implicit by default; remove the '#' qualifier here", tpos t))
        else Exp_tapp(e, t)
      | Inr e', imp -> 
        Exp_app(e, e', imp))
    e args 
    
let mk_tapp t args =
  List.fold_left
    (fun t -> function
      | Inl t', imp -> 
        withkind kun <| Typ_app(t, t',imp)
      | Inr e, imp -> 
        withkind kun <| Typ_dep(t, e, imp))
    t args 

let op_as_vlid env arity r s =
  let r l = Some <| set_lid_range l r in
  match s, env.phase with
    | "=", _ ->    r Const.op_Eq
    | ":=", _ ->   r Const.op_ColonEq
    | "<", _ ->    r Const.op_LT
    | "<=", _ ->   r Const.op_LTE
    | ">", _ ->    r Const.op_GT
    | ">=", _ ->   r Const.op_GTE
    | "&&", _ ->   r Const.op_And
    | "||", _ ->   r Const.op_Or
    | "*", Type 
    | "*", Formula -> r Const.mul_lid
    | "*", _ -> r Const.op_Multiply
    | "+", Type 
    | "+", Formula -> r Const.add_lid
    | "+", _ ->    r Const.op_Addition
    | "-", Type when (arity=1) -> r Const.minus_lid
    | "-", Formula when (arity=1) -> r Const.minus_lid
    | "-", _    when (arity=1) -> r Const.op_Minus
    | "-", Type -> r Const.sub_lid
    | "-", Formula -> r Const.sub_lid
    | "-", _ ->    r Const.op_Subtraction
    | "/", Type -> r Const.div_lid
    | "/", Formula -> r Const.div_lid
    | "/", _ ->    r Const.op_Division
    | "%", Type -> r Const.modulo_lid
    | "%", Formula -> r Const.modulo_lid
    | "%", _ ->    r Const.op_Modulus
    | "!", _ ->    r Const.read_lid
    | "@", _ ->    r Const.list_append_lid
    | "^", _ ->    r Const.strcat_lid
    | "|>", _ ->   r Const.pipe_right_lid
    | "<|", _ ->   r Const.pipe_left_lid
    | "<>", _ ->   r Const.op_notEq
    | _ -> None

let op_as_tylid r s =
  let r l = Some <| set_lid_range l r in
  match s with
    | "=" ->    r Const.eq2_lid
    | "<>" ->   r Const.neq2_lid
    | "<" ->    r Const.lt_lid
    | "<=" ->   r Const.lte_lid
    | ">" ->    r Const.gt_lid
    | ">=" ->   r Const.gte_lid
    | "/\\" ->  r Const.and_lid
    | "\\/" ->  r Const.or_lid
    | "==>" ->  r Const.implies_lid
    | "<==>" -> r Const.iff_lid
    | _ -> None

let rec is_type env (t:term) =
  if t.level = Type then true
  else match (unparen t).term with
    | Wild -> true
    | Op("*", hd::_)                    (* tuple constructor *)
    | Op("=", hd::_) -> is_type env hd  (* equality predicate *)
    | Op("<", _)
    | Op("<=", _)
    | Op(">", _)
    | Op(">=", _) -> env.phase = Formula
    | Op(s, _) -> (match op_as_tylid t.range s with
        | None -> false
        | _ -> true)
    | QForall _
    | QExists _
    | Sum _
    | Refine _
    | Tvar _ -> true
    | Var l
    | Name l
    | Construct(l, _) -> is_type_lid env l
    | Abs(_, t)
    | App(t, _, _)
    | Paren t
    | Ascribed(t, _)
    | Product(_, t)  -> is_type env t
    | _ -> false

let rec is_kind (t:term) : bool =
  if t.level = Kind
  then true
  else match (unparen t).term with
    | Name {str="Type"} -> true
    | Product(_, t) -> is_kind t
    | Paren t -> is_kind t
    | _ -> false

let rec is_type_binder env b =
  if b.blevel = Formula
  then match b.binder with
    | Variable _
    | Annotated _ -> false
    | TAnnotated _
    | TVariable _ -> true
    | NoName t -> is_kind  t
  else match b.binder with
    | Variable _ -> raise (Error("Unexpected binder without annotation", b.brange))
    | TVariable _ -> false
    | TAnnotated _ -> true
    | Annotated (_, t)
    | NoName t -> is_kind t

let sort_ftv ftv =
  Util.sort_with (fun x y -> String.compare x.idText y.idText) <|
      Util.remove_dups (fun x y -> x.idText = y.idText) ftv

let rec free_type_vars_b env binder = match binder.binder with
  | Variable x -> env, []
  | TVariable x ->
    let env, _ = push_local_tbinding env x in
    (env, [x])
  | Annotated(id, term) ->
    (env, free_type_vars env term)
  | TAnnotated(id, term) ->
    let env, _ = push_local_tbinding env id in
    (env, [])
  | NoName t ->
    (env, free_type_vars env t)
and free_type_vars env t = match (unparen t).term with
  | Tvar a ->
    (match DesugarEnv.try_lookup_typ_var env a with
      | None -> [a]
      | _ -> [])

  | Wild
  | Const _
  | Var  _
  | Name _  -> []

  | Paren t
  | Ascribed(t, _) -> free_type_vars env t

  | Construct(_, ts) -> List.collect (fun (t, _) -> free_type_vars env t) ts

  | Op(_, ts) -> List.collect (free_type_vars env) ts

  | App(t1,t2,_) -> free_type_vars env t1@free_type_vars env t2

  | Refine (b, t) ->
    let env, f = free_type_vars_b env b in
    f@free_type_vars env t
      
  | Product(binders, body)
  | Sum(binders, body) ->
    let env, free = List.fold_left (fun (env, free) binder ->
      let env, f = free_type_vars_b env binder in
      env, f@free) (env, []) binders in
    free@free_type_vars env body

  | Abs _  (* not closing implicitly over free vars in type-level functions *)
  | If _
  | QForall _
  | QExists _ -> [] (* not closing implicitly over free vars in formulas *)
  | Let _
  | Affine _
  | Project  _
  | Record _
  | Match _
  | TryWith _
  | Seq _ -> error "Unexpected type in free_type_vars computation" t t.range
    
let close env t =
  let ftv = sort_ftv <| free_type_vars env t in
  if List.length ftv = 0
  then t
  else let binders = ftv |> List.map (fun x -> mk_binder (TAnnotated(x, kind_star x.idRange)) x.idRange Type false) in
       let result = mk_term (Product(binders, t)) t.range t.level in
       result
         
let rec is_app_pattern p = match p.pattern with
  | PatAscribed(p,_) -> is_app_pattern p
  | PatApp({pattern=PatVar id}, _) -> true
  | _ -> false

let rec destruct_app_pattern env is_top_level p = match p.pattern with
  | PatAscribed(p,t) ->
    let (name, args, _) = destruct_app_pattern env is_top_level p in
    (name, args, Some t)
  | PatApp({pattern=PatVar id}, args) when is_top_level ->
    (Inr (qualify env id), args, None)
  | PatApp({pattern=PatVar id}, args) ->
    (Inl id, args, None)
  | _ ->
    failwith "Not an app pattern"

type bnd = 
  | TBinder of btvdef * kind
  | VBinder of bvvdef * typ
  | LetBinder of lident * typ
type env_t = DesugarEnv.env
type lenv_t = list<either<btvdef,bvvdef>>
let rec desugar_data_pat env (p:pattern) : (env_t * bnd * Syntax.pat) =
  let resolvex (l:lenv_t) e x = 
    match l |> Util.find_opt (function Inr y -> y.ppname.idText=x.idText | _ -> false) with 
      | Some (Inr y) -> l, e, y
      | _ -> 
        let e, x = push_local_vbinding e x in
        (Inr x::l), e, x in
  let resolvea (l:lenv_t) e a = 
    match l |> Util.find_opt (function Inl b -> b.ppname.idText=a.idText | _ -> false) with 
      | Some (Inl b) -> l, e, b
      | _ -> 
        let e, a = push_local_tbinding e a in
        (Inl a::l), e, a in
  let rec aux (loc:lenv_t) env (p:pattern) =
    let pos q = Pat_withinfo(q, p.prange) in 
    match p.pattern with
      | PatOr [] -> failwith "impossible"
      | PatOr (p::ps) ->
        let env0 = env in
        let q = p in
        let loc, env, var, p = aux loc env p in
        let loc, env, ps = List.fold_left (fun (loc, env, ps) p ->
          let loc, env, _, p = aux loc env p in
          loc, env, p::ps) (loc, env, []) ps in
        let pat = Pat_disj (p::List.rev ps) in
        ignore (check_pat_vars q.prange pat);
        loc, env, var, pos <| pat

      | PatAscribed(p, t) ->
        let loc, env, binder, p = aux loc env p in
        let binder = match binder with
          | LetBinder _ -> failwith "impossible"
          | TBinder (x, _) -> TBinder(x, desugar_kind env t)
          | VBinder (x, _) -> VBinder(x, desugar_typ env t) in
        loc, env, binder, p

      | PatTvar a ->
        if a.idText = "'_"
        then loc, env, TBinder(new_bvd (Some p.prange), Kind_unknown), Pat_twild
        else let loc, env, abvd = resolvea loc env a in
             loc, env, TBinder(abvd, Kind_unknown), pos <| Pat_tvar abvd

      | PatWild ->
        let x = new_bvd (Some p.prange) in
        loc, env, VBinder(x, tun), pos <| Pat_wild
        
      | PatConst c ->
        let x = new_bvd (Some p.prange) in
        loc, env, VBinder(x, tun), pos <| Pat_constant c

      | PatVar x ->
        let loc, env, xbvd = resolvex loc env x in 
        loc, env, VBinder(xbvd, tun), pos <| Pat_var xbvd

      | PatName l ->
        let l = fail_or (try_lookup_datacon env) l in
        let x = new_bvd (Some p.prange) in
        loc, env, VBinder(x, tun), pos <| Pat_cons(l.v, [])

      | PatApp({pattern=PatName l}, args) ->
        let loc, env, args = List.fold_right (fun arg (loc,env,args) ->
          let loc, env, _, arg = aux loc env arg in
          (loc, env, arg::args)) args (loc, env, []) in
        let l = fail_or (try_lookup_datacon env) l in
        let x = new_bvd (Some p.prange) in
        loc, env, VBinder(x, tun), pos <| Pat_cons(l.v, args)

      | PatApp _ -> raise (Error ("Unexpected pattern", p.prange))

      | PatList pats ->
        let loc, env, pats = List.fold_right (fun pat (loc, env, pats) ->
          let loc,env,_,pat = aux loc env pat in
          loc, env, pat::pats) pats (loc, env, []) in
        let pat = List.fold_right (fun hd tl -> Pat_cons(Const.cons_lid, [hd;tl])) pats (Pat_cons(Const.nil_lid, [])) in
        let x = new_bvd (Some p.prange) in
        loc, env, VBinder(x, tun), pos <| pat
        
      | PatTuple(args) ->
        let loc, env, args = List.fold_left (fun (loc, env, pats) p ->
          let loc, env, _, pat = aux loc env p in
          loc, env, pat::pats) (loc,env,[]) args in
        let args = List.rev args in
        let constr = fail_or (DesugarEnv.try_lookup_lid env) (lid_of_path [Util.strcat "MkTuple" (Util.string_of_int <| List.length args)] p.prange) in
        let l = match constr with
          | Exp_fvar (v, _) -> v.v
          | _ -> failwith "impossible" in
        let x = new_bvd (Some p.prange) in
        loc, env, VBinder(x, tun), pos <| Pat_cons(l, args)

      | PatRecord ([]) -> 
        raise (Error ("Unexpected pattern", p.prange))
        
      | PatRecord (fields) -> 
        let (f, _) = List.hd fields in 
        let record, _ = fail_or (try_lookup_record_by_field_name env) f in
        let fields = fields |> List.map (fun (f, p) -> 
          (fail_or (qualify_field_to_record env record) f, p)) in
        let args = record.fields |> List.map (fun (f, _) -> 
          match fields |> List.tryFind (fun (g, _) -> lid_equals f g) with 
            | None -> mk_pattern PatWild p.prange
            | Some (_, p) -> p) in 
        let app = mk_pattern (PatApp(mk_pattern (PatName record.constrname) p.prange, args)) p.prange in
        aux loc env app in
  let _, env, b, p = aux [] env p in
  env, b, p
         
and desugar_binding_pat_maybe_top top env p : (env_t * bnd * option<pat>) =
  if top 
  then match p.pattern with 
    | PatVar x -> (env, LetBinder(qualify env x, tun), None)
    | PatAscribed({pattern=PatVar x}, t) -> 
      (env, LetBinder(qualify env x, desugar_typ env t), None)
    | _ -> raise (Error("Unexpected pattern at the top-level", p.prange)) 
  else
    let (env, binder, p) = desugar_data_pat env p in
    let p = match p with
      | Pat_var _
      | Pat_tvar _ 
      | Pat_withinfo (Pat_var _, _) 
      | Pat_withinfo (Pat_tvar _, _) -> None
      | _ -> Some p in
    (env, binder, p)

and desugar_binding_pat env = desugar_binding_pat_maybe_top false env

and desugar_match_pat_maybe_top top env pat =
  let (env, binder, pat) = desugar_data_pat env pat in
  (env, pat)

and desugar_match_pat env = desugar_match_pat_maybe_top false env 

and desugar_typ_or_exp (env:env_t) (t:term) : either<typ,exp> =
  if is_type env t
  then Inl <| desugar_typ env t
  else Inr <| desugar_exp env t

and desugar_exp env e = desugar_exp_maybe_top false env e 

and desugar_exp_maybe_top (top_level:bool) (env:env_t) (top:term) : exp =
  let pos e = Exp_meta(Meta_info(e, tun, top.range)) in
  let getpos = function
    | Exp_meta(Meta_info(_, _, p)) -> p
    | _ -> failwith "impossible" in
  begin match (unparen top).term with
    | Const c -> pos <| Exp_constant c

    | Op(s, args) ->
      begin match op_as_vlid env (List.length args) top.range s with
        | None -> raise (Error("Unexpected operator: " ^ s, top.range))
        | Some l ->
          let op = fvar l (range_of_lid l) in
          let args = args |> List.map (fun t -> desugar_typ_or_exp env t, false) in
          pos <| mk_app op args
      end

    | Var l
    | Name l -> pos <| fail_or (DesugarEnv.try_lookup_lid env) l

    | Construct(l, args) ->
      let dt = pos <| Exp_fvar(fail_or (DesugarEnv.try_lookup_datacon env) l, true) in
      let args = List.map (fun (t, imp) -> desugar_typ_or_exp env t, imp) args in
      pos <| Exp_meta(Meta_desugared(mk_app dt args, Data_app)) 

    | Abs(binders, body) ->
      let ftv = List.fold_left (fun ftvs pat ->
        match pat.pattern with
          | PatAscribed(_, t) -> free_type_vars env t@ftvs
          | _ -> ftvs) [] binders in
      let ftv = sort_ftv ftv in
      let binders = (ftv |> List.map (fun a -> mk_pattern (PatTvar a) top.range))@binders in
      let rec desugar_abs env a = match a.term with
        | Abs((p::q::qtl), body) ->
          let rest = q::qtl in
          desugar_abs env
            (mk_term (Abs([p], mk_term (Abs(rest, body)) (Range.union_ranges q.prange body.range) Expr))
               top.range Expr)
            
        | Abs([p], body) ->
          let env, binder, pat = desugar_binding_pat env p in
          begin match binder with
            | LetBinder _ -> failwith "impossible"
            | TBinder (a, k) ->
              let body = desugar_abs env body in
              pos <| Exp_tabs(a, k, body)
            | VBinder (x, t) ->
              let body = desugar_exp env body in
              let body = match pat with
                | None -> body
                | Some pat -> Exp_meta(Meta_info(Exp_match(bvd_to_exp x t, [(pat, None, body)]), tun, getpos body)) in
              pos <| Exp_abs(x, t, body)
          end

        | _ -> desugar_exp env a
      in
      desugar_abs env (mk_term (Abs(binders, body)) top.range top.level)
        
    | App({term=Var a}, arg, _) when (lid_equals a Const.assert_lid
                                   || lid_equals a Const.assume_lid) ->
      let phi = desugar_formula env arg in
      pos <| Exp_app(pos <| Exp_tapp(fvar a (range_of_lid a), phi),
                     pos <| Exp_constant(Const_unit), false)

    | App(e, t, imp) ->
      let e = desugar_exp env e in
      let t = desugar_typ_or_exp env t in
      pos <| mk_app e [(t,imp)]
        
    | Seq(t1, t2) ->
      pos <| Exp_meta(Meta_desugared(desugar_exp env (mk_term (Let(false, [(mk_pattern PatWild t1.range,t1)], t2)) top.range Expr), 
                              Sequence))
        
    | Let(b, ((pat, _snd)::_tl), body) when is_app_pattern pat ->
      let bindings = (pat, _snd)::_tl in
      let funs = bindings |> List.map (fun (p, def) ->
        if not <| is_app_pattern p
        then raise (Error("Only functions may be defined recursively", p.prange))
        else (destruct_app_pattern env top_level p, def)) in
      let env', fnames =
        List.fold_left (fun (env, fnames) ((f, _, _), _) ->
          let env, lbname = match f with
            | Inl x -> 
              let env, xx = push_local_vbinding env x in
              env, Inl xx
            | Inr l -> 
              push_rec_binding env (Binding_let l), Inr l in
          env, (lbname::fnames)) (env, []) funs in
      let fnames = List.rev fnames in
      let desugar_one_def env ((_, args, result_t), def) =
        let def = match result_t with
          | None -> def
          | Some t -> mk_term (Ascribed(def, t)) (Range.union_ranges t.range def.range) Expr in
        let def = mk_term (Abs(args, def)) top.range top.level in
        desugar_exp env def in
      let defs = funs |> List.map (desugar_one_def (if b then env' else env)) in
      let lbs = List.map2 (fun lbname def -> (lbname, tun, def)) fnames defs in
      let body = desugar_exp env' body in
      pos <| Exp_let((b, lbs), body)
    
    | Let(false, [(pat, t1)], t2) ->
      let t1 = desugar_exp env t1 in
      let env, binder, pat = desugar_binding_pat_maybe_top top_level env pat in
      begin match binder with
        | TBinder _ -> failwith "Unexpected type binder in let"
        | LetBinder(l, t) -> 
          let body = desugar_exp env t2 in
          pos <| Exp_let((false, [(Inr l, t, t1)]), body)
        | VBinder (x,t) ->
          let body = desugar_exp env t2 in
          let body = match pat with
            | None 
            | Some Pat_wild -> body
            | Some pat -> Exp_meta(Meta_info(Exp_match(bvd_to_exp x t, [(pat, None, body)]), tun, getpos body)) in
          pos <| Exp_let((false, [(Inl x, t, t1)]), body)
      end

    | If(t1, t2, t3) ->
      pos <| Exp_match(desugar_exp env t1,
                       [(Pat_constant (Const_bool true), None, desugar_exp env t2);
                        (Pat_constant (Const_bool false), None, desugar_exp env t3)])

    | TryWith(e, branches) -> 
      let r = top.range in 
      let handler = mk_function branches r r in 
      let body = mk_function [(mk_pattern (PatConst Const_unit) r, None, e)] r r in 
      let a1 = mk_term (App(mk_term (Var Const.try_with_lid) r top.level, body, false)) r  top.level in 
      let a2 = mk_term (App(a1, handler, false)) r top.level in 
      desugar_exp env a2

    | Match(e, branches) ->
      let desugar_branch (pat, wopt, b) =
        let env, pat = desugar_match_pat env pat in
        let wopt = match wopt with
          | None -> None
          | Some e -> Some <| desugar_exp env e in
        let b = desugar_exp env b in
        (pat, wopt, b) in
      pos <| Exp_match(desugar_exp env e, List.map desugar_branch branches)

    | Ascribed(e, t) ->
      pos <| Exp_ascribed(desugar_exp env e, desugar_typ env t)

    | Record(eopt, []) ->
      raise (Error("Unexpected empty record", top.range))

    | Record(eopt, fields) ->
      let f, _ = List.hd fields in
      let record, _ = fail_or (try_lookup_record_by_field_name env) f in
      let get_field xopt f =
        let fn = f.ident in
        let found = fields |> Util.find_opt (fun (g, _) ->
          let gn = g.ident in
          fn.idText = gn.idText) in
        match found with
          | Some (_, e) -> e
          | None ->
            match xopt with
              | None ->
                raise (Error (Util.format1 "Field %s is missing" (text_of_lid f), top.range))
              | Some x ->
                mk_term (Project(x, f)) x.range x.level in
      let recterm = match eopt with
        | None ->
          Construct(record.constrname,
                    record.fields |> List.map (fun (f, _) ->
                      get_field None f, false))
            
        | Some e ->
          let x = genident (Some e.range) in
          let xterm = mk_term (Var (lid_of_ids [x])) x.idRange Expr in
          let record = Construct(record.constrname,
                                 record.fields |> List.map (fun (f, _) ->
                                    get_field (Some xterm) f, false)) in
          Let(false, [(mk_pattern (PatVar x) x.idRange, e)], mk_term record top.range top.level) in
      let recterm = mk_term recterm top.range top.level in
      desugar_exp env recterm
        
    | Project(e, f) ->
      //let _ = Util.print_string (Util.format1 "Looking up field name %s\n" (Print.sli f)) in 
      let _, fieldname = fail_or (try_lookup_record_by_field_name env) f in
      //let _ = Util.print_string (Util.format2 "resolved %s as %s\n" (Print.sli f) (Print.sli fieldname)) in 
      let proj = mk_term (App(mk_term (Var fieldname) (range_of_lid f) Expr, e, false)) top.range top.level in
      desugar_exp env proj

    | Paren e ->
      desugar_exp env e

    | _ ->
      error "Unexpected term" top top.range
  end
  
and desugar_typ env (top:term) : typ =
  let pos t = Typ_meta (Meta_pos(withkind kun t, top.range)) in
  let pk t = withkind kun <| pos t in
  let setpos t = match t.t with 
    | Typ_meta(Meta_pos(t', _)) -> withkind t.k <| pos t'.t
    | t' -> withkind t.k <| pos t' in
  let top = unparen top in  
  match top.term with
    | Wild -> withkind kun <| Typ_unknown

    | Op("*", [t1; _]) when is_type env t1 ->
      let rec flatten t = match t.term with
        | Op("*", [t1;t2]) -> 
          let binders, final = flatten t2 in
          let b = mk_binder (NoName t1) t1.range Type false in
          b::binders, final
        | Sum(binders, final) -> binders, final 
        | _ -> [], t in 
      let binders, final = flatten top in
      let t = mk_term (Sum(binders, final)) top.range top.level in
      desugar_typ env t

    | Op("*", [t1;_]) -> 
      raise (Error(Util.format1 "The operator \"*\" is resolved here as multiplication since \"%s\" is a term, although a type was expected" (term_to_string t1), top.range))
      
    | Op("<>", args) ->
      let t = desugar_typ env (mk_term (Op("=", args)) top.range top.level) in
      pk <| Typ_app(ftv <| set_lid_range Const.not_lid top.range, t, false)

    | Op(s, args) ->
      begin match op_as_tylid top.range s with
        | None -> raise (Error("Unrecognized type operator" ^ s, top.range))
        | Some l ->
          let args = List.map (fun t -> desugar_typ_or_exp env t, false) args in
          mk_tapp (ftv l) args
      end

    | Tvar a ->
      setpos <| fail_or2 (try_lookup_typ_var env) a
          
    | Var l
    | Name l ->
      setpos <| fail_or (try_lookup_typ_name env) l
      
    | Construct(l, args) ->
      let t = fail_or (try_lookup_typ_name env) l in
      let args = List.map (fun (t, imp) -> desugar_typ_or_exp env t, imp) args in
      mk_tapp t args

    | Abs(p::(q::_rest), body) ->
      let rest = q::_rest in
      let body = mk_term (Abs(rest, body)) (Range.union_ranges q.prange body.range) top.level in
      let t = mk_term (Abs([p], body)) top.range top.level in
      desugar_typ env t

    | Abs([p], body) ->
      let env, binder, pat = desugar_binding_pat env p in
      begin match pat with
        | Some q -> raise (Error(Util.format1 "Pattern matching at the type level is not supported; got %s\n" (Print.pat_to_string q) , p.prange))
        | None ->
          let body = desugar_typ env body in
          match binder with
            | LetBinder _ -> failwith "impossible"
            | TBinder (a,k) -> pk <| Typ_tlam(a, k, body)
            | VBinder (x,t) -> pk <| Typ_lam(x, t, body)
      end

    | App(t1, t2, imp) ->
      let t1 = desugar_typ env t1 in
      let t2 = desugar_typ_or_exp env t2 in
      mk_tapp t1 [(t2,imp)]
        
    | Product([], t) ->
      desugar_typ env t
      
    | Product(b::(_bs::_bstl), t) ->
      let bs = _bs::_bstl in
      desugar_typ env (mk_term (Product([b], mk_term (Product(bs, t)) top.range top.level)) top.range top.level)

    | Product([b], t) ->
      let tk = desugar_binder env b in
      let p = match tk with
        | Inl(None, k) ->
          let x = new_bvd (Some b.brange) in
          Typ_univ(x, k, desugar_typ env t)

        | Inl(Some x, k) ->
          let env, x = push_local_tbinding env x in
          Typ_univ(x, k, desugar_typ env t)

        | Inr(None, t1) ->
          Typ_fun(None, t1, desugar_typ env t, b.implicit)

        | Inr(Some x, t1) ->
          let env, x = push_local_vbinding env x in
          Typ_fun(Some x, t1, desugar_typ env t, b.implicit) in
      pk p

    | Refine(b,f) ->
      begin match desugar_exp_binder env b with
        | (None, _) -> failwith "Missing binder in refinement"
        | (Some x, t) ->
          let env, x = push_local_vbinding env x in
          let f = desugar_formula env f in
          pk <| Typ_refine(x, t, f)
      end

    | Paren t ->
      desugar_typ env t

    | Ascribed(t,k) ->
      pk <| Typ_ascribed(desugar_typ env t, desugar_kind env k)

    | Sum(binders, t) ->
      let env, _, targs = List.fold_left (fun (env, tparams, typs) b ->
        let xopt, t = desugar_exp_binder env b in
        let env, x = match xopt with
          | None -> env, new_bvd (Some top.range)
          | Some x -> push_local_vbinding env x in
        (env, tparams@[Tparam_term(x, t)], typs@[close_with_lam tparams t]))
        (env, [], []) (binders@[mk_binder (NoName t) t.range Type false]) in
      let tup = fail_or (try_lookup_typ_name env) (lid_of_path [Util.strcat "Tuple" (Util.string_of_int <| List.length targs)] top.range) in
      List.fold_left (fun t1 t2 -> pk <| Typ_app(t1,t2,false)) tup targs
          
    | Record _ -> failwith "Unexpected record type"

    | _ -> error "Expected a type" top top.range

and desugar_kind env k : kind =
  match (unparen k).term with
    | Name {str="Type"} -> Kind_star
    | Wild           -> Kind_unknown
    | Product([b], t) ->
      let tk = desugar_binder env b in
      begin match tk with
        | Inl(None, k) ->
          Kind_tcon(None, k, desugar_kind env t, b.implicit)

        | Inl(Some a, k) ->
          let env, a = push_local_tbinding env a in
          Kind_tcon(Some a, k, desugar_kind env t, b.implicit)

        | Inr(None, t0) ->
          Kind_dcon(None, t0, desugar_kind env t, b.implicit)

        | Inr(Some x, t0) ->
          let env, x = push_local_vbinding env x in
          Kind_dcon(Some x, t0, desugar_kind env t, b.implicit)
      end
    | _ -> error "Unexpected term where kind was expected" k k.range

and desugar_formula' env (f:term) : typ =
  let connective s = match s with
    | "/\\"  -> Some Const.and_lid
    | "\\/"  -> Some Const.or_lid
    | "==>"  -> Some Const.implies_lid
    | "<==>" -> Some Const.iff_lid
    | _ -> None in
  let pos t = Typ_meta (Meta_pos(t, f.range)) in
  let wk t = withkind kun t in
  let pk t = wk <| pos (wk t) in
  let desugar_quant (q:lident) (qt:lident) b pats body =
    let tk = desugar_binder env ({b with blevel=Formula}) in
    match tk with
      | Inl(Some a,k) ->
        let env, a = push_local_tbinding env a in
        let pats = List.map (desugar_typ_or_exp env) pats in
        let body = desugar_formula env body in
        let body = match pats with
          | [] -> body
          | _ -> wk <| Typ_meta (Meta_pattern(body, pats)) in
        let body = pk <| Typ_tlam(a, k, body) in
        wk (pos <| mk_tapp (ftv (set_lid_range qt b.brange)) [Inl body, false])
            
      | Inr(Some x,t) ->
        let env, x = push_local_vbinding env x in
        let pats = List.map (desugar_typ_or_exp env) pats in
        let body = desugar_formula env body in
        let body = match pats with
          | [] -> body
          | _ -> withkind kun <| Typ_meta (Meta_pattern(body, pats)) in
        let body = wk <| Typ_lam(x, t, body) in
        wk (pos <| mk_tapp (ftv (set_lid_range q b.brange)) [Inl body, false])

      | _ -> failwith "impossible" in
            
  let push_quant q (binders:list<binder>) pats (body:term) = match binders with
    | b::(b'::_rest) ->
      let rest = b'::_rest in
      let body = mk_term (q(rest, pats, body)) (Range.union_ranges b'.brange body.range) Formula in
      mk_term (q([b], [], body)) f.range Formula
    | _ -> failwith "impossible" in

  match (unparen f).term with
    | Op("=", ((hd::_args))) ->
      let args = hd::_args in
      let args = List.map (fun t -> desugar_typ_or_exp env t, false) args in
      let eq =
        if is_type env hd
        then ftv (set_lid_range Const.eqTyp_lid f.range)
        else ftv (set_lid_range Const.eq2_lid f.range) in
      mk_tapp eq args

    | Op(s, args) ->
      begin match connective s, args with
        | Some conn, [_;_] ->
          mk_tapp
            (ftv (set_lid_range conn f.range))
            (List.map (fun x -> Inl <| desugar_formula env x, false) args)
        | _ -> desugar_typ env f
      end

    | App({term=Var l}, arg, imp) when (l.str = "not") ->
      let arg = desugar_formula env arg in
      mk_tapp (ftv (set_lid_range Const.not_lid f.range)) [Inl arg, imp]
        
    | If(f1, f2, f3) ->
      mk_tapp
        (ftv (set_lid_range Const.ite_lid f.range))
        (List.map (fun x -> Inl <| desugar_typ env x, false) [f1;f2;f3])

    | QForall((_1::_2::_3), pats, body) ->
      let binders = _1::_2::_3 in
      desugar_formula env (push_quant QForall binders pats body)

    | QExists((_1::_2::_3), pats, body) ->
      let binders = _1::_2::_3 in
      desugar_formula env (push_quant QExists binders pats body)
        
    | QForall([b], pats, body) ->
      desugar_quant Const.forall_lid Const.allTyp_lid b pats body

    | QExists([b], pats, body) ->
      desugar_quant Const.exists_lid Const.allTyp_lid b pats body

    | Paren f ->
      desugar_formula env f

    | _ when is_type env f ->
      desugar_typ env f

    | _ ->
      error "Expected a formula" f f.range

and desugar_formula env t =
  desugar_formula' ({env with phase=Formula}) t

and desugar_binder env b =
  if is_type_binder env b
  then Inl (desugar_type_binder env b)
  else Inr (desugar_exp_binder env b)

and typars_of_binders env bs =
    let env, tpars = List.fold_left (fun (env, out) b ->
        let tk = desugar_binder env ({b with blevel=Formula}) in  (* typars follow the same binding conventions as formulas *)
        match tk with
            | Inl(Some a,k) ->
                let env, a = push_local_tbinding env a in
                (env, Tparam_typ(a,k)::out)
            | Inr(Some x,t) ->
                let env, x = push_local_vbinding env x in
                (env, Tparam_term(x,t)::out)
            | _ -> raise (Error ("Unexpected binder", b.brange))) (env, []) bs in
    env, List.rev tpars

and desugar_exp_binder env b = match b.binder with
  | Annotated(x, t) -> Some x, desugar_typ env t
  | TVariable t -> None, fail_or2 (try_lookup_typ_var env) t
  | NoName t -> None, desugar_typ env t
  | Variable x -> Some x, tun
  | _ -> raise (Error("Unexpected domain of an arrow or sum (expected a type)", b.brange))
    
and desugar_type_binder env b = match b.binder with
  | TAnnotated(x, t) -> Some x, desugar_kind env t
  | NoName t -> None, desugar_kind env t
  | TVariable x -> Some x, Kind_star
  | _ -> raise (Error("Unexpected domain of an arrow or sum (expected a kind)", b.brange))

let get_logic_tag = function
  | LogicTag l -> [l]
  | _ -> []

let logic_tags q = List.collect get_logic_tag q

let mk_data_ops env = function
  | Sig_datacon(lid, t, _, _) ->
    let args, tconstr = collect_formals t in
    //Printf.printf "Collecting formals from type %s; got %s with args %d\n" (Print.typ_to_string t) (Print.typ_to_string tconstr) (List.length args);
    let argpats = args |> List.map (function
      | Inr(Some x,_) -> Pat_var x
      | Inr(None,targ) -> Pat_var (new_bvd (Some (range_of_lid lid)))
      | Inl(a,_) -> Pat_tvar a) in
    let freetv, freexv  = freevars_typ tconstr in
    let freevars = args |> List.filter (function
      | Inl(a,k) -> freetv |> Util.for_some (fun b -> Util.bvd_eq a b.v)
      | Inr(Some x,t) -> freexv |> Util.for_some (fun y -> Util.bvd_eq x y.v)
      | _ -> false) in
    //Printf.printf "Got %d free vars\n" (List.length freevars);
    let freeterms = freevars |> List.map (function 
      | Inl (a, k) -> Inl (Util.bvd_to_typ a k), false
      | Inr (Some x, t) -> Inr (Util.bvd_to_exp x t), false
      | _ -> failwith "impossible") in
    let formal = new_bvd (Some <| range_of_lid lid) in
    let formal_exp = bvd_to_exp formal tconstr in
    let rec build_exp freevars e = match freevars with
      | Inl (a,k)::rest -> Exp_tabs(a, k, build_exp rest e)
      | Inr (Some x, t)::rest -> Exp_abs(x, t, build_exp rest e)
      | _::rest -> failwith "impossible"
      | [] -> Exp_abs(formal, tconstr, e) in
    let rec build_typ freevars t = match freevars with
      | Inl (a,k)::rest -> withkind kun <| Typ_univ(a, k, build_typ rest t)
      | Inr (xopt,tt)::rest -> withkind kun <| Typ_fun(xopt, tt, build_typ rest t, false)
      | [] -> withkind kun <| Typ_fun(Some formal, tconstr, t, false) in
    let rec build_kind freevars k = match freevars with
      | Inl (a,kk)::rest -> Kind_tcon(Some a, kk, build_kind rest k, false)
      | Inr (xopt,t)::rest -> Kind_dcon(xopt, t, build_kind rest k, false)
      | [] -> Kind_dcon(Some formal, tconstr, k, false) in
    let build_exp  = build_exp freevars in
    let build_typ  = build_typ freevars in
    let build_kind = build_kind freevars in
    let subst_to_string s = 
      List.map (function 
        | Inl (a, t) -> Util.format2 "(%s -> %s)" (Print.strBvd a) (Print.typ_to_string t)  
        | Inr (x, e) -> Util.format2 "(%s -> %s)" (Print.strBvd x) (Print.exp_to_string e)) s 
      |> String.concat ", " in  
    let subst_typ s t =
      //Printf.printf "Substituting [%s] in type\n%s\n" (subst_to_string s) (Print.typ_to_string t);
      let res = subst_typ s t in  res in
      //Printf.printf "Got\n%s\n" (Print.typ_to_string res); flush stdout; res in
    let rec aux fields t = match (compress_typ t).t with
      | Typ_fun(Some x, t1, t2, _) ->
        let field_name = lid_of_ids (ids_of_lid lid @ [x.ppname]) in
        let t = build_typ t1 in
        let body = ewithpos (Exp_match(formal_exp, [(Pat_cons(lid, argpats), None, bvd_to_exp x t1)])) x.ppname.idRange in
        let sigs =
          [Sig_val_decl(field_name, t, Some Assumption, Some Logic_projector, range_of_lid field_name)
           //;Sig_let((false, [(Inr field_name, t, build_exp body)]))
          ] in
        //let _ = Util.print_string (Util.format2 "adding value projector %s at type %s\n" field_name.str (Print.typ_to_string t)) in 
        let t2 = 
          if freexv |> Util.for_some (fun y -> Util.bvd_eq x y.v)
          then t2
          else 
            let subst = [Inr(x, mk_app (fvar field_name (range_of_lid field_name)) (freeterms@[Inr formal_exp, false]))] in
            subst_typ subst t2 in
        aux (fields@sigs) t2
          
      | Typ_univ(a, k, t2) ->
        let field_name = lid_of_ids (ids_of_lid lid @ [a.ppname]) in
        let kk = build_kind k in
        let sigs = Sig_tycon(field_name, [], kk, [], [], [Logic_projector], range_of_lid field_name) in
        //let _ = Util.print_string (Util.format2 "adding type projector %s at type %s\n" field_name.str (Print.kind_to_string kk)) in 
        let t2 = 
          if freetv |> Util.for_some (fun b -> Util.bvd_eq b.v a)
          then t2
          else let subst = [Inl(a, mk_tapp (ftv field_name) (freeterms@[Inr formal_exp, false]))] in
               subst_typ subst t2 in
        aux (fields@[sigs]) t2

      | _ -> fields in
    
    aux [] t
  | _ -> []

let rec desugar_tycon env rng quals tcs : (env_t * sigelts) =
  let tycon_id = function
    | TyconAbstract(id, _, _)
    | TyconAbbrev(id, _, _, _)
    | TyconRecord(id, _, _, _)
    | TyconVariant(id, _, _, _) -> id in
  let binder_to_term b = match b.binder with
    | Annotated (x, _)
    | Variable x -> mk_term (Var (lid_of_ids [x])) x.idRange Expr
    | TAnnotated(a, _)
    | TVariable a -> mk_term (Tvar a) a.idRange Type
    | NoName t -> t in
  let apply_binders t binders =
    List.fold_left (fun out b -> mk_term (App(out, binder_to_term b, false)) out.range out.level)
      t binders in
  let tycon_record_as_variant = function
    | TyconRecord(id, parms, kopt, fields) ->
      let constrName = mk_ident("Mk" ^ id.idText, id.idRange) in
      let fields = List.map (fun (x,t) -> mk_binder (Annotated(x,t)) x.idRange Expr false) fields in
      let result = apply_binders (mk_term (Var (lid_of_ids [id])) id.idRange Type) parms in
      let constrTyp = mk_term (Product(fields, result)) id.idRange Type in
      (* let _ = Util.print_string (Util.format2 "Translated record %s to constructor %s\n" (id.idText) (term_to_string constrTyp)) in *)
      TyconVariant(id, parms, kopt, [(constrName, Some constrTyp, false)])
    | _ -> failwith "impossible" in
  let desugar_abstract_tc quals _env mutuals = function
    | TyconAbstract(id, binders, kopt) ->
      let _env', typars = typars_of_binders _env binders in
      let k = match kopt with
        | None -> Kind_unknown
        | Some k -> desugar_kind _env' k in
      let tconstr = apply_binders (mk_term (Var (lid_of_ids [id])) id.idRange Type) binders in
      let qlid = qualify _env id in
      let se = Sig_tycon(qlid, typars, k, mutuals, [], logic_tags quals, rng) in
      let _env = push_rec_binding _env (Binding_tycon qlid) in
      let _env2 = push_rec_binding _env' (Binding_tycon qlid) in
      _env, (_env2, typars), se, tconstr
    | _ -> failwith "Unexpected tycon" in
  let push_tparam env = function
    | Tparam_term(x,_) -> push_bvvdef env x
    | Tparam_typ(a,_) -> push_btvdef env a in
  let push_tparams = List.fold_left push_tparam in
  match tcs with
    | [TyconAbstract _] ->
        let tc = List.hd tcs in
        let _, _, se, _ = desugar_abstract_tc quals env [] tc in
        let env = push_sigelt env se in
        (* let _ = pr "Pushed %s\n" (text_of_lid (qualify env (tycon_id tc))) in *)
        env, [se]

    | [TyconAbbrev(id, binders, kopt, t)] ->
        let env', typars = typars_of_binders env binders in
        let k = match kopt with
            | None -> Kind_unknown
            | Some k -> desugar_kind env' k in
        let t = desugar_typ env' t in
        let se = Sig_typ_abbrev(qualify env id, typars, k, t, rng) in
        let env = push_sigelt env se in
        env, [se]

    | [TyconRecord _] ->
      let trec = List.hd tcs in
      desugar_tycon env rng (LogicTag Logic_record::quals) [tycon_record_as_variant trec]

    |  _::_ ->
      let env0 = env in
      let mutuals = List.map (fun x -> qualify env <| tycon_id x) tcs in
      let rec collect_tcs (env, tcs, quals) tc = match tc with
        | TyconRecord _ ->
          let trec = tc in
          collect_tcs (env, tcs, LogicTag Logic_record::quals) (tycon_record_as_variant trec)
        | TyconVariant(id, binders, kopt, constructors) ->
          let env, (_, tps), se, tconstr = desugar_abstract_tc quals env mutuals (TyconAbstract(id, binders, kopt)) in
          env, Inl(se, tps, constructors, tconstr)::tcs, quals
        | TyconAbbrev(id, binders, kopt, t) ->
          let env, (_, tps), se, tconstr = desugar_abstract_tc quals env mutuals (TyconAbstract(id, binders, kopt)) in
          env, Inr(se, tps, t)::tcs, quals
        | _ -> failwith "Unrecognized mutual type definition" in
      let env, tcs, quals = List.fold_left collect_tcs (env, [], quals) tcs in
      let tcs = List.rev tcs in
      let sigelts = tcs |> List.collect (function
        | Inr(Sig_tycon(id, tpars, k, _, _, _, _), tps, t) ->
          let env_tps = push_tparams env tps in
          let t = desugar_typ env_tps t in
          [Sig_typ_abbrev(id, tpars, k, t, rng)]
            
        | Inl (Sig_tycon(tname, tpars, k, mutuals, _, tags, _), tps, constrs, tconstr) ->
          let env_tps = push_tparams env tps in
          let constrNames, constrs = List.split <|
              (constrs |> List.map (fun (id, topt, of_notation) ->
                let t =
                  if of_notation
                  then match topt with 
                    | Some t -> mk_term (Product([mk_binder (NoName t) t.range t.level false], tconstr)) t.range t.level
                    | None -> tconstr 
                  else match topt with 
                    | None -> failwith "Impossible"
                    | Some t -> t in
                let t = desugar_typ env_tps (close env_tps t) in
                let name = qualify env id in
                (name, Sig_datacon(name, close_typ tps t, tname, rng)))) in
              Sig_tycon(tname, tpars, k, mutuals, constrNames, tags, rng)::constrs
        | _ -> failwith "impossible") in
      let bundle = Sig_bundle(sigelts, rng) in
      let env = push_sigelt env0 bundle in
      let data_ops =
        if logic_tags quals |> Util.for_some (function
          | Logic_data
          | Logic_record -> true
          | _ -> false)
        then sigelts |> List.collect (mk_data_ops env)
        else [] in
      let env = List.fold_left push_sigelt env data_ops in
      env, [bundle]@data_ops

    | [] -> failwith "impossible"
           
let desugar_decl env (d:decl) : (env_t * sigelts) = match d.decl with
  | Open lid ->
    let env = DesugarEnv.push_namespace env lid in
    env, []
  
  | Tycon(qual, tcs) ->
    desugar_tycon env d.drange [qual] tcs
          
  | ToplevelLet(isrec, lets) ->
    begin match compress_exp <| desugar_exp_maybe_top true env (mk_term (Let(isrec, lets, mk_term (Const Const_unit) d.drange Expr)) d.drange Expr) with
        | Exp_let(lbs, _) ->
          let s = Sig_let(lbs, d.drange) in
          let env = push_sigelt env s in
          env, [s]
        | _ -> failwith "Desugaring a let did not produce a let"
    end

  | Main t ->
    let e = desugar_exp env t in
    let se = Sig_main(e, d.drange) in
    env, [se]

  | Assume(atag, id, t) ->
    let f = desugar_formula env t in
    env, [Sig_assume(qualify env id, f, Public, Assumption, d.drange)]

  | Val(LogicTag Logic_val, id, t) ->
    let t = desugar_typ env (close env t) in
    let se = Sig_logic_function(qualify env id, t, [], d.drange) in
    let env = push_sigelt env se in
    env, [se]

  | Val(AST.Assumption, id, t) -> 
    let t = desugar_typ env (close env t) in
    let se = Sig_val_decl(qualify env id, t, Some Assumption, None, d.drange) in
    let env = push_sigelt env se in
    env, [se]

  | Val(NoQual, id, t) ->
    let t = desugar_typ env (close env t) in
    let se = Sig_val_decl(qualify env id, t, None, None, d.drange) in
    let env = push_sigelt env se in
    env, [se]

  | Exception(id, None) ->
    let t = fail_or (try_lookup_typ_name env) Const.exn_lid in
    let se = Sig_datacon(qualify env id, t, Const.exn_lid, d.drange) in
    let env = push_sigelt env se in
    env, [se]
        
  | Exception(id, Some term) ->
    let t = desugar_typ env term in
    let t = withkind kun <| Typ_fun(None, t, fail_or (try_lookup_typ_name env) Const.exn_lid, false) in
    let se = Sig_datacon(qualify env id, t, Const.exn_lid, d.drange) in
    let env = push_sigelt env se in
    env, [se]

  | _ -> raise (Error("Unexpected declaration", d.drange))
        
let desugar_modul env (m:AST.modul) : env_t * Syntax.modul = match m with
  | Module(mname, decls) ->
    let env = DesugarEnv.prepare_module env mname in
    let env, sigelts = List.fold_left (fun (env, sigelts) d ->
      let env, se = desugar_decl env d in
      env, sigelts@se) (env, []) decls in
    let modul = {
      name = mname;
      declarations = sigelts;
      exports = []
    } in
    let env = DesugarEnv.finish_module env modul in
    env, modul
  
let desugar_file env (pragmas, ms) =
  let env, mods = List.fold_left (fun (env, mods) m ->
    let env, m = desugar_modul env m in
    finish_module env m, m::mods) (env, []) ms in
  env, List.rev mods

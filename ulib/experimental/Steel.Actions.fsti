(*
   Copyright 2019 Microsoft Research

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
module Steel.Actions
open Steel.Heap
open Steel.HProp
open Steel.Memory
open FStar.Real
open Steel.Permissions
module U32 = FStar.UInt32

////////////////////////////////////////////////////////////////////////////////
// Actions:
// sel, split, update
////////////////////////////////////////////////////////////////////////////////
let pre_action (fp:hprop) (a:Type) (fp':a -> hprop) =
  hheap fp -> (x:a & hheap (fp' x))

let is_frame_preserving #a #fp #fp' (f:pre_action fp a fp') =
  forall frame h0.
    interp (fp `star` frame) h0 ==>
    (let (| x, h1 |) = f h0 in
     interp (fp' x `star` frame) h1)

let action_depends_only_on_fp (#pre:_) (#a:_) (#post:_) (f:pre_action pre a post)
  = forall (h0:hheap pre)
      (h1:heap {disjoint h0 h1})
      (post: (x:a -> fp_prop (post x))).
      (interp pre (join h0 h1) /\ (
       let (| x0, h |) = f h0 in
       let (| x1, h' |) = f (join h0 h1) in
       x0 == x1 /\
       (post x0 h <==> post x1 h')))

let action (fp:hprop) (a:Type) (fp':a -> hprop) =
  f:pre_action fp a fp'{ is_frame_preserving f /\
                         action_depends_only_on_fp f }


let pre_m_action (fp:hprop) (a:Type) (fp':a -> hprop) =
  hmem fp -> (x:a & hmem (fp' x))

val m_action_depends_only_on (#pre:hprop) (#a:Type) (#post:a -> hprop) (f:pre_m_action pre a post) : prop

val is_m_frame_preserving (#a:Type) (#fp:hprop) (#fp':a -> hprop) (f:pre_m_action fp a fp') : prop

let m_action (fp:hprop) (a:Type) (fp':a -> hprop) =
  f:pre_m_action fp a fp'{ is_m_frame_preserving f /\ m_action_depends_only_on f }

val frame_fp_prop (#fp:_) (#a:Type) (#fp':_) (act:action fp a fp')
                  (#frame:hprop) (q:fp_prop frame)
   : Lemma (forall (h0:hheap (fp `star` frame)).
              (affine_star fp frame h0;
               q h0 ==>
               (let (| x, h1 |) = act h0 in
                q h1)))